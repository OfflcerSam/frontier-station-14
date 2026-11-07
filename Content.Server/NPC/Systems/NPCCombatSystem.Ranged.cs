using System.Numerics; // Frontier
using Content.Server.NPC.Components;
using Content.Shared.CombatMode;
using Content.Shared.Interaction;
using Content.Shared.Physics;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Random; // Frontier

namespace Content.Server.NPC.Systems;

public sealed partial class NPCCombatSystem
{
    [Dependency] private readonly SharedCombatModeSystem _combat = default!;
    [Dependency] private readonly RotateToFaceSystem _rotate = default!;

    private EntityQuery<CombatModeComponent> _combatQuery;
    private EntityQuery<NPCSteeringComponent> _steeringQuery;
    private EntityQuery<RechargeBasicEntityAmmoComponent> _rechargeQuery;
    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<TransformComponent> _xformQuery;

    // TODO: Don't predict for hitscan
    private const float ShootSpeed = 20f;

    /// <summary>
    /// Cooldown on raycasting to check LOS.
    /// </summary>
    public const float UnoccludedCooldown = 0.2f;

    private void InitializeRanged()
    {
        _combatQuery = GetEntityQuery<CombatModeComponent>();
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _rechargeQuery = GetEntityQuery<RechargeBasicEntityAmmoComponent>();
        _steeringQuery = GetEntityQuery<NPCSteeringComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();

        SubscribeLocalEvent<NPCRangedCombatComponent, ComponentStartup>(OnRangedStartup);
        SubscribeLocalEvent<NPCRangedCombatComponent, ComponentShutdown>(OnRangedShutdown);
    }

    private void OnRangedStartup(EntityUid uid, NPCRangedCombatComponent component, ComponentStartup args)
    {
        if (TryComp<CombatModeComponent>(uid, out var combat))
        {
            _combat.SetInCombatMode(uid, true, combat);
        }
        else
        {
            component.Status = CombatStatus.Unspecified;
        }
    }

    private void OnRangedShutdown(EntityUid uid, NPCRangedCombatComponent component, ComponentShutdown args)
    {
        if (TryComp<CombatModeComponent>(uid, out var combat))
        {
            _combat.SetInCombatMode(uid, false, combat);
        }
    }

    private void UpdateRanged(float frameTime)
    {
        var query = EntityQueryEnumerator<NPCRangedCombatComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var comp, out var xform))
        {
            if (comp.Status == CombatStatus.Unspecified)
                continue;

            // Frontier: only the current target's movement goal can fail or force pursuit.
            NPCSteeringComponent? steering = null;
            if (comp.PursuitGoal is { } goal && _steeringQuery.TryComp(uid, out var pursuit) &&
                pursuit.Coordinates.Equals(goal))
            {
                steering = pursuit;
            }

            if (steering?.Status == SteeringStatus.NoPath ||
                !_xformQuery.TryComp(comp.Target, out var targetXform) ||
                !_physicsQuery.TryComp(comp.Target, out var targetBody) ||
                targetXform.MapID != xform.MapID)
            {
                comp.Status = CombatStatus.TargetUnreachable;
                ResetRangedPerception(comp);
                continue;
            }
            // End Frontier

            if (_combatQuery.TryGetComponent(uid, out var combatMode))
            {
                _combat.SetInCombatMode(uid, true, combatMode);
            }

            if (!_gun.TryGetGun(uid, out var gunUid, out var gun))
            {
                comp.Status = CombatStatus.NoWeapon;
                ResetRangedPerception(comp); // Frontier: abandon aim for the missing weapon.
                continue;
            }

            var ammoEv = new GetAmmoCountEvent();
            RaiseLocalEvent(gunUid, ref ammoEv);

            if (ammoEv.Count == 0)
            {
                // Frontier: reacquire line of sight before firing after recharge.
                comp.PreparedShot = null;
                comp.LOSAccumulator = 0f;
                // End Frontier
                // Recharging then?
                if (_rechargeQuery.HasComponent(gunUid))
                {
                    continue;
                }

                comp.Status = CombatStatus.Unspecified;
                comp.ShootAccumulator = 0f;
                continue;
            }

            comp.LOSAccumulator -= frameTime;

            var worldPos = _transform.GetWorldPosition(xform);
            var targetPos = _transform.GetWorldPosition(targetXform);

            var distance = (targetPos - worldPos).Length(); // Frontier: perception uses the real target position.
            var oldInLos = comp.TargetInLOS;

            // TODO: Should be doing these raycasts in parallel
            // Ideally we'd have 2 steps, 1. to go over the normal details for shooting and then 2. to handle beep / rotate / shoot
            if (comp.LOSAccumulator <= 0f) // Frontier: check immediately after a target change.
            {
                comp.LOSAccumulator += UnoccludedCooldown;

                // For consistency with NPC steering.
                var collisionGroup = comp.UseOpaqueForLOSChecks ? CollisionGroup.Opaque : (CollisionGroup.Impassable | CollisionGroup.InteractImpassable);
                comp.TargetInLOS = _interaction.InRangeUnobstructed(uid, comp.Target, distance + 0.1f, collisionGroup);
            }

            if (!comp.TargetInLOS)
            {
                comp.ShootAccumulator = 0f;
                comp.PreparedShot = null; // Frontier: discard the shot when line of sight is lost.
                comp.Status = CombatStatus.NotInSight;

                if (steering != null) // Frontier: stationary guns do not control unrelated movement.
                {
                    steering.ForceMove = true;
                }

                continue;
            }

            if (!oldInLos && comp.SoundTargetInLOS != null)
            {
                _audio.PlayPvs(comp.SoundTargetInLOS, uid);
            }

            comp.Status = CombatStatus.Normal; // Frontier: visibility is independent of the gun's readiness.
            comp.ShootAccumulator += frameTime;

            if (comp.ShootAccumulator < comp.ShootDelay)
            {
                continue;
            }

            var mapVelocity = targetBody.LinearVelocity;
            // Frontier: sample only for a ready weapon. Keep the offset while turning and
            // continue to update the lead from the target's real position and velocity.
            var canShoot = Enabled && _gun.CanShoot(gun);
            if (!Enabled || comp.PreparedShot?.Gun != gunUid)
                comp.PreparedShot = null;

            if (canShoot && comp.PreparedShot == null)
            {
                var offset = _random.Prob(comp.MissChance) ? _random.NextVector2(1f, 2f) : Vector2.Zero;
                comp.PreparedShot = (gunUid, offset);
            }

            var targetSpot = targetPos + mapVelocity * distance / ShootSpeed +
                (comp.PreparedShot?.Offset ?? Vector2.Zero);
            // End Frontier

            // If we have a max rotation speed then do that.
            var goalRotation = (targetSpot - worldPos).ToWorldAngle();
            var rotationSpeed = comp.RotationSpeed;

            if (!_rotate.TryRotateTo(uid, goalRotation, frameTime, comp.AccuracyThreshold, rotationSpeed?.Theta ?? double.MaxValue, xform))
            {
                continue;
            }

            // TODO: LOS
            // TODO: Ammo checks
            // TODO: Burst fire
            // TODO: Cycling
            // Max rotation speed

            // TODO: Check if we can face

            if (!canShoot) // Frontier
                continue;

            EntityCoordinates targetCordinates;

            if (_mapManager.TryFindGridAt(xform.MapID, targetPos, out var gridUid, out var mapGrid))
            {
                targetCordinates = new EntityCoordinates(gridUid, _map.WorldToLocal(gridUid, mapGrid, targetSpot));
            }
            else
            {
                targetCordinates = new EntityCoordinates(xform.MapUid!.Value, targetSpot);
            }

            comp.PreparedShot = null; // Frontier: the next attack attempt gets its own sample.
            _gun.AttemptShoot(uid, gunUid, gun, targetCordinates, comp.Target);
        }
    }
}
