using System.Numerics;
using Content.Server.Destructible;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.Storage.EntitySystems;
using Content.Shared.ActionBlocker;
using Content.Shared.Damage;
using Content.Shared.Examine;
using Content.Shared.Hands.Components;
using Content.Shared.Lock;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Components;
using Content.Shared.Physics;
using Content.Shared.Storage.Components;
using Content.Shared.Tools.Systems;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Profiling;

namespace Content.Server.NPC.Systems;

public sealed partial class NPCCombatSystem
{
    [Dependency] private readonly SharedContainerSystem _storageContainers = default!;
    [Dependency] private readonly EntityStorageSystem _storage = default!;
    [Dependency] private readonly ExamineSystemShared _storageExamine = default!;
    [Dependency] private readonly ActionBlockerSystem _storageActions = default!;
    [Dependency] private readonly LockSystem _storageLocks = default!;
    [Dependency] private readonly WeldableSystem _storageWelds = default!;
    [Dependency] private readonly SharedPhysicsSystem _storagePhysics = default!;
    [Dependency] private readonly PathfindingSystem _storagePaths = default!;
    [Dependency] private readonly ProfManager _storageProfile = default!;

    private const float StorageSightRange = 14f;
    private const CollisionGroup StorageShotMask = CollisionGroup.Impassable | CollisionGroup.InteractImpassable |
        CollisionGroup.BulletImpassable | CollisionGroup.Opaque;
    private static readonly TimeSpan StorageAttemptLimit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StorageDamageLimit = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Report containment separately from a usable exterior storage entity. Bags and other
    /// unsupported containers still block attacks against their contents.
    /// </summary>
    internal bool FindStorageBlocker(EntityUid target, out EntityUid? blocker)
    {
        blocker = null;
        var contained = false;
        var entity = target;
        for (var depth = 0; depth < 16; depth++)
        {
            if (!_storageContainers.TryGetContainingContainer(entity, out var container))
                return contained;

            contained = true;
            entity = container.Owner;
            if (TerminatingOrDeleted(entity) || !TryComp<EntityStorageComponent>(entity, out var storage) ||
                storage.Open || storage.Contents != container)
            {
                blocker = null;
                return true;
            }

            blocker = entity;
        }

        blocker = null;
        return contained;
    }

    internal bool CanKeepStorageTarget(EntityUid owner, EntityUid target, EntityUid storage, PathFlags flags)
    {
        if (owner == storage || !HasComp<InputMoverComponent>(owner) || _storageContainers.IsEntityInContainer(owner) ||
            (!CanOperateStorage(owner, flags) && (flags & PathFlags.Smashing) == 0))
        {
            return false;
        }

        if (!TryComp<NPCStorageCombatComponent>(owner, out var attempt) ||
            attempt.Hostile != target || attempt.Storage != storage)
        {
            return true;
        }

        return _timing.CurTime < attempt.Deadline && (!attempt.Failed || !SameStorageAttempt(attempt, target, storage));
    }

    internal bool RemembersStorageTarget(EntityUid owner, EntityUid target, EntityUid? storage = null) =>
        TryComp<NPCStorageCombatComponent>(owner, out var attempt) && attempt.Hostile == target &&
        (storage == null || attempt.Storage == storage) && _timing.CurTime < attempt.Deadline;

    private bool SameStorageAttempt(NPCStorageCombatComponent attempt, EntityUid target, EntityUid storage) =>
        attempt.Hostile == target && attempt.Storage == storage &&
        attempt.Welded == _storageWelds.IsWelded(storage) && attempt.Locked == _storageLocks.IsLocked(storage);

    private bool CanOperateStorage(EntityUid owner, PathFlags flags) =>
        (flags & PathFlags.Interact) != 0 && HasComp<HandsComponent>(owner) && _storageActions.CanComplexInteract(owner);

    private bool HandleCombatStorage(EntityUid uid, EntityUid target, NPCRangedCombatComponent? ranged,
        float frameTime, out CombatStatus status)
    {
        status = CombatStatus.Normal;
        // Escape and other explicit attacks on objects use their ordinary attack path.
        if (!HasComp<MobStateComponent>(target) || !FindStorageBlocker(target, out var blocker))
        {
            EndStorageMovement(uid, restorePursuit: ranged != null, forget: true);
            return false;
        }

        // Ranged plans without a background MoveTo cannot approach storage.
        if (blocker is not { } storage || storage == uid || !HasComp<InputMoverComponent>(uid) ||
            _storageContainers.IsEntityInContainer(uid) ||
            (ranged != null && ranged.PursuitGoal == null))
        {
            EndStorageMovement(uid, restorePursuit: false, forget: true);
            status = CombatStatus.TargetUnreachable;
            return true;
        }

        var flags = _steeringQuery.TryComp(uid, out var steering) ? steering.Flags : _storagePaths.GetFlags(uid);
        var attempt = EnsureComp<NPCStorageCombatComponent>(uid);
        if (!SameStorageAttempt(attempt, target, storage))
        {
            EndStorageMovement(uid, restorePursuit: ranged != null, forget: false);
            // A lock or weld change permits another method within the original budget.
            if (attempt.Hostile != target || attempt.Storage != storage)
                attempt.Deadline = _timing.CurTime + StorageAttemptLimit;

            attempt.Hostile = target;
            attempt.Storage = storage;
            attempt.Welded = _storageWelds.IsWelded(storage);
            attempt.Locked = _storageLocks.IsLocked(storage);
            attempt.TriedOpen = false;
            attempt.Failed = false;
            attempt.ApproachDirection = null;
            attempt.ApproachIndex = 0;
            attempt.UseMelee = false;
            attempt.LastDamage = 0f;
            attempt.ShotsWithoutProgress = 0;
            attempt.DamageDeadline = null;
        }

        if (attempt.Failed || _timing.CurTime >= attempt.Deadline ||
            !_storageExamine.InRangeUnOccluded(uid, storage, StorageSightRange))
        {
            status = FailStorageAttempt(uid, attempt);
            return true;
        }

        // The lock API enforces access. Only attempt immediate unlocks here, since a timed
        // unlock would need a separately owned do-after.
        var lockComp = CompOrNull<LockComponent>(storage);
        var canUnlock = lockComp is { UnlockTime: var time } && time == TimeSpan.Zero &&
            (lockComp.UnlockOnClick || lockComp.ShowLockVerbs);
        if (!attempt.TriedOpen && !attempt.Welded && (!attempt.Locked || canUnlock) && CanOperateStorage(uid, flags))
        {
            if (!_interaction.InRangeAndAccessible(uid, storage))
            {
                status = ApproachStorage(uid, attempt, ranged);
                return true;
            }

            attempt.TriedOpen = true;
            var opened = _storageActions.CanInteract(uid, storage) &&
                (!attempt.Locked || _storageLocks.TryUnlock(storage, uid, lockComp)) &&
                _storage.TryOpenStorage(uid, storage, silent: true) && _storage.IsOpen(storage);
            if (_storageProfile.IsEnabled)
                _storageProfile.WriteValue("NPC storage opened", opened ? 1 : 0);
            if (opened)
            {
                return true;
            }

            // Unlocking can succeed even if an open event is vetoed. Treat it as one attempt.
            attempt.Locked = _storageLocks.IsLocked(storage);
        }

        if ((flags & PathFlags.Smashing) == 0 || !HasComp<DestructibleComponent>(storage))
        {
            status = FailStorageAttempt(uid, attempt);
            return true;
        }

        if (ranged != null && !attempt.UseMelee)
        {
            status = ShootStorage(uid, attempt, ranged, frameTime);
            return true;
        }

        status = SmashStorage(uid, attempt, ranged);
        return true;
    }

    private CombatStatus SmashStorage(EntityUid uid, NPCStorageCombatComponent attempt, NPCRangedCombatComponent? ranged)
    {
        if (!Enabled)
            return CombatStatus.Normal;

        return _steering.SmashObstacle(uid, attempt.Storage) switch
        {
            NPCSteeringSystem.SteeringObstacleStatus.Approaching => ApproachStorage(uid, attempt, ranged),
            NPCSteeringSystem.SteeringObstacleStatus.Continuing => HoldStoragePosition(uid, attempt, ranged),
            _ => FailStorageAttempt(uid, attempt)
        };
    }

    private CombatStatus SwitchToMeleeBreach(EntityUid uid, NPCStorageCombatComponent attempt, NPCRangedCombatComponent ranged)
    {
        attempt.UseMelee = true;
        return SmashStorage(uid, attempt, ranged);
    }

    private CombatStatus ShootStorage(EntityUid uid, NPCStorageCombatComponent attempt, NPCRangedCombatComponent ranged,
        float frameTime)
    {
        var storage = attempt.Storage;
        if (!TryComp<DamageableComponent>(storage, out var damage) || !_gun.TryGetGun(uid, out var gunUid, out var gun))
            return SwitchToMeleeBreach(uid, attempt, ranged);

        var amount = damage.TotalDamage.Float();
        if (attempt.ShotsWithoutProgress > 0 && amount > attempt.LastDamage)
        {
            attempt.ShotsWithoutProgress = 0;
            attempt.DamageDeadline = null;
        }

        var direction = _transform.GetWorldPosition(storage) - _transform.GetWorldPosition(uid);
        if (!_interaction.InRangeUnobstructed(uid, storage, direction.Length() + 0.1f, StorageShotMask))
            return ApproachStorage(uid, attempt, ranged);

        HoldStoragePosition(uid, attempt, ranged);
        if (!Enabled || !_gun.CanShoot(gun) ||
            !_rotate.TryRotateTo(uid, direction.ToWorldAngle(), frameTime, ranged.AccuracyThreshold,
                ranged.RotationSpeed?.Theta ?? double.MaxValue))
        {
            return CombatStatus.Normal;
        }

        var ammo = new GetAmmoCountEvent();
        RaiseLocalEvent(gunUid, ref ammo);
        if (ammo.Count == 0)
        {
            return _rechargeQuery.HasComp(gunUid)
                ? CombatStatus.Normal
                : SwitchToMeleeBreach(uid, attempt, ranged);
        }

        // Count only real shots and evaluate progress when the gun can fire again.
        if (attempt.ShotsWithoutProgress >= 2 &&
            attempt.DamageDeadline is { } deadline && _timing.CurTime >= deadline)
        {
            return SwitchToMeleeBreach(uid, attempt, ranged);
        }

        var previousFire = gun.NextFire;
        // The crate itself must be the explicit shot target for both projectiles and hitscan.
        _gun.AttemptShoot(uid, gunUid, gun, new EntityCoordinates(storage, Vector2.Zero), storage);
        if (gun.NextFire == previousFire)
            return SwitchToMeleeBreach(uid, attempt, ranged);

        attempt.LastDamage = amount;
        attempt.ShotsWithoutProgress++;
        attempt.DamageDeadline ??= _timing.CurTime + StorageDamageLimit;
        return CombatStatus.Normal;
    }

    private CombatStatus ApproachStorage(EntityUid uid, NPCStorageCombatComponent attempt, NPCRangedCombatComponent? ranged)
    {
        if (attempt.Goal is { } goal && !attempt.HoldingPosition &&
            _steeringQuery.TryComp(uid, out var steering) && steering.Coordinates.Equals(goal))
        {
            if (steering.Status == SteeringStatus.Moving)
                return CombatStatus.Normal;

            attempt.ApproachIndex++;
        }

        var storage = attempt.Storage;
        if (attempt.ApproachDirection == null)
        {
            var offset = _transform.GetWorldPosition(uid) - _transform.GetWorldPosition(storage);
            var direction = offset.LengthSquared() > 0.001f ? Vector2.Normalize(offset) : Vector2.UnitX;
            attempt.ApproachDirection = (-_transform.GetWorldRotation(storage)).RotateVec(direction);
        }

        var bounds = _storagePhysics.GetWorldAABB(storage);
        var distance = MathF.Max(0.4f, bounds.Size.Length() / 2f) + 0.3f;
        for (; attempt.ApproachIndex < 4; attempt.ApproachIndex++)
        {
            var rotation = Angle.FromDegrees(attempt.ApproachIndex * 90);
            var local = rotation.RotateVec(attempt.ApproachDirection.Value * distance);
            var candidate = new EntityCoordinates(storage, local);
            if (_storagePaths.GetPoly(candidate) is { } node && _steering.FindObstacleEntities(uid, node) != null)
                continue;

            // Entity-relative placement keeps the exterior point attached to a moving crate.
            SetStorageGoal(uid, attempt, ranged, candidate, holdingPosition: false);
            return CombatStatus.Normal;
        }

        return FailStorageAttempt(uid, attempt);
    }

    private CombatStatus HoldStoragePosition(EntityUid uid, NPCStorageCombatComponent attempt, NPCRangedCombatComponent? ranged)
    {
        if (!attempt.HoldingPosition || attempt.Goal == null)
            SetStorageGoal(uid, attempt, ranged, _transform.GetMoverCoordinates(uid), holdingPosition: true);

        return CombatStatus.Normal;
    }

    private void SetStorageGoal(EntityUid uid, NPCStorageCombatComponent attempt, NPCRangedCombatComponent? ranged,
        EntityCoordinates goal, bool holdingPosition)
    {
        if (attempt.Goal == null && ranged?.PursuitGoal is { } pursuit &&
            _steeringQuery.TryComp(uid, out var previous) && previous.Coordinates.Equals(pursuit))
        {
            attempt.Pursuit = new(pursuit, previous.Range, previous.ArriveOnLineOfSight,
                previous.InRangeMaxSpeed, previous.DirectMove);
        }

        var movement = _steering.Register(uid, goal);
        movement.Range = holdingPosition ? 0.1f : 0.25f;
        movement.InRangeMaxSpeed = 0.05f;
        movement.ArriveOnLineOfSight = false;
        var grid = _transform.GetGrid(uid);
        movement.DirectMove = grid == null || grid != _transform.GetGrid(goal);
        attempt.Goal = goal;
        attempt.HoldingPosition = holdingPosition;
    }

    private CombatStatus FailStorageAttempt(EntityUid uid, NPCStorageCombatComponent attempt)
    {
        attempt.Failed = true;
        EndStorageMovement(uid, restorePursuit: false, forget: false);
        if (_steeringQuery.TryComp(uid, out var pursuit) && pursuit.Coordinates.EntityId == attempt.Hostile)
            _steering.Unregister(uid, pursuit);
        return CombatStatus.TargetUnreachable;
    }

    private void EndStorageMovement(EntityUid uid, bool restorePursuit, bool forget)
    {
        if (!TryComp<NPCStorageCombatComponent>(uid, out var attempt))
            return;

        if (attempt.Goal is { } goal && _steeringQuery.TryComp(uid, out var movement) && movement.Coordinates.Equals(goal))
        {
            if (restorePursuit && attempt.Pursuit is { } saved)
            {
                _steering.Register(uid, saved.Goal, movement);
                movement.Range = saved.Range;
                movement.ArriveOnLineOfSight = saved.ArriveOnLineOfSight;
                movement.InRangeMaxSpeed = saved.InRangeMaxSpeed;
                movement.DirectMove = saved.DirectMove;
            }
            else
            {
                _steering.Unregister(uid, movement);
            }
        }

        attempt.Goal = null;
        attempt.HoldingPosition = false;
        attempt.Pursuit = null;
        if (forget)
            RemComp<NPCStorageCombatComponent>(uid);
    }
}
