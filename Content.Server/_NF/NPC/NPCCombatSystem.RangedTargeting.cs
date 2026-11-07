using System.Numerics;
using Content.Server.NPC.Components;
using Robust.Shared.Map;

namespace Content.Server.NPC.Systems;

public sealed partial class NPCCombatSystem
{
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
