using System.Numerics;
using Content.Server.NPC.Components;
using Content.Shared.Damage.Components;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.Physics;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;

namespace Content.Server.NPC.Systems;

public sealed partial class NPCCombatSystem
{
    [Dependency] private readonly SharedPhysicsSystem _shotPhysics = default!;
    [Dependency] private readonly NpcFactionSystem _shotFactions = default!;

    private void OnRangedShotAttempted(EntityUid uid, NPCRangedCombatComponent component, ref ShotAttemptedEvent args)
    {
        // Check the live aim immediately before ammo is taken, including scheduled burst rounds.
        // A body crossing the shot does not change target perception or retire pursuit.
        var gun = args.Used.Comp;
        if (args.Cancelled || args.User != uid || HasComp<ActorComponent>(uid) ||
            !TryComp<NpcFactionMemberComponent>(uid, out var faction) || gun.NextFire > _timing.CurTime ||
            gun.ShootCoordinates is not { } aim)
        {
            return;
        }

        var origin = _transform.GetMapCoordinates(uid);
        var target = _transform.ToMapCoordinates(aim);
        var direction = target.Position - origin.Position;
        var length = direction.Length();
        if (origin.MapId != target.MapId || length < 0.001f)
            return;

        var ray = new CollisionRay(origin.Position, direction / length, (int) CollisionGroup.BulletImpassable);
        foreach (var hit in _shotPhysics.IntersectRay(origin.MapId, ray, length, uid, returnOnFirstHit: false))
        {
            // Crates with targeted-only collision are skipped by ordinary fire past them.
            var entity = hit.HitEntity;
            if (entity != gun.Target && CompOrNull<RequireProjectileTargetComponent>(entity)?.Active == true)
                continue;

            if (TryComp<NpcFactionMemberComponent>(entity, out var other) &&
                _shotFactions.IsEntityFriendly((uid, faction), (entity, other)))
            {
                args.Cancel();
            }

            // The nearest projectile blocker determines whether the shot would hit an ally.
            return;
        }
    }

    internal EntityCoordinates? SetRangedTarget(Entity<NPCRangedCombatComponent> ent, EntityUid target,
        EntityCoordinates? previousPursuit)
    {
        var (uid, combat) = ent;
        if (combat.Target != target)
        {
            // A fresh GunOperator can resume the same known storage after HTN teardown.
            var resumesStorage = !combat.Target.IsValid() && TryComp<NPCStorageCombatComponent>(uid, out var attempt) &&
                attempt.Hostile == target;
            if (!resumesStorage)
                EndStorageMovement(uid, restorePursuit: true, forget: true);

            _retaliation.RetireObservation(uid, combat.Target);
        }
        combat.Target = target;
        combat.PursuitGoal = null;
        ResetRangedPerception(combat);
        var hasTarget = _xformQuery.TryComp(target, out var targetXform);
        if (!hasTarget && combat.Status != CombatStatus.Unspecified)
            combat.Status = CombatStatus.TargetUnreachable;
        else if (combat.Status is CombatStatus.TargetUnreachable or CombatStatus.NotInSight)
            combat.Status = CombatStatus.Normal;

        if (previousPursuit is not { } previous)
            return null;

        var goal = hasTarget ? new EntityCoordinates(target, Vector2.Zero) : (EntityCoordinates?) null;
        // Only the background MoveTo owned by this plan may be retargeted.
        if (_steeringQuery.TryComp(uid, out var steering) && steering.Coordinates.Equals(previous))
        {
            if (goal is { } destination)
            {
                _steering.Register(uid, destination, steering);
                var grid = Transform(uid).GridUid;
                steering.DirectMove = grid == null || grid != targetXform!.GridUid;
                combat.PursuitGoal = destination;
            }
            else
            {
                _steering.Unregister(uid, steering);
            }
        }

        return goal;
    }

    private static void ResetRangedPerception(NPCRangedCombatComponent combat)
    {
        combat.LOSAccumulator = 0f;
        combat.TargetInLOS = false;
        combat.ShootAccumulator = 0f;
        combat.PreparedShot = null;
    }
}
