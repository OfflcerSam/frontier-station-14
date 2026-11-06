using Content.Server.Destructible;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Climbing;
using Content.Shared.CombatMode;
using Content.Shared.Damage; // Frontier
using Content.Shared.DoAfter;
using Content.Shared.Doors.Components;
using Content.Shared.NPC;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Utility;
using ClimbableComponent = Content.Shared.Climbing.Components.ClimbableComponent;
using ClimbingComponent = Content.Shared.Climbing.Components.ClimbingComponent;

namespace Content.Server.NPC.Systems;

public sealed partial class NPCSteeringSystem
{
    /*
     * For any custom path handlers, e.g. destroying walls, opening airlocks, etc.
     * Putting it onto steering seemed easier than trying to make a custom compound task for it.
     * I also considered task interrupts although the problem is handling stuff like pathfinding overlaps
     * Ideally we could do interrupts but that's TODO.
     */

    /*
     * TODO:
     * - Add path cap
     * - Circle cast BFS in LOS to determine targets.
     * - Store last known coordinates of X targets.
     * - Require line of sight for melee
     * - Add new behavior where they move to melee target's last known position (diffing theirs and current)
     *  then do the thing like from dishonored where it gets passed to a search system that opens random stuff.
     *
     * Also need to make sure it picks nearest obstacle path so it starts smashing in front of it.
     */


    private SteeringObstacleStatus TryHandleFlags(EntityUid uid, NPCSteeringComponent component, PathPoly poly, bool moving) // Frontier: only stop for actions that require it.
    {
        // Frontier: handle the current colliders rather than an anchored or stale navmesh snapshot.
        var obstacleEnts = new List<EntityUid>();
        if (FindObstacleEntities(uid, poly, obstacleEnts) != null)
        {
            if (component.ObstacleActionTarget is { } target && !obstacleEnts.Contains(target))
                CancelObstacleAction(component);

            var id = component.DoAfterId;

            // Still doing what we were doing before.
            var doAfterStatus = _doAfter.GetStatus(id);

            switch (doAfterStatus)
            {
                case DoAfterStatus.Running:
                    return SteeringObstacleStatus.Continuing;
                case DoAfterStatus.Cancelled:
                    return SteeringObstacleStatus.Failed;
            }

            var isDoor = obstacleEnts.Exists(ent => HasComp<DoorComponent>(ent));
            var isAccessRequired = (poly.Data.Flags & PathfindingBreadcrumbFlag.Access) != 0x0;
            var isClimbable = obstacleEnts.Exists(ent => HasComp<ClimbableComponent>(ent));

            // End Frontier

            // Just walk into it stupid
            if (isDoor && !isAccessRequired)
            {
                var doorQuery = GetEntityQuery<DoorComponent>();

                // ... At least if it's not a bump open.
                foreach (var ent in obstacleEnts)
                {
                    if (!doorQuery.TryGetComponent(ent, out var door))
                        continue;

                    if (!door.BumpOpen && (component.Flags & PathFlags.Interact) != 0x0)
                    {
                        if (door.State != DoorState.Opening)
                        {
                            if (moving) // Frontier: settle before interacting, including when a door is also smashable.
                                return SteeringObstacleStatus.Continuing;

                            _interaction.InteractionActivate(uid, ent);
                            return SteeringObstacleStatus.Continuing;
                        }
                    }
                }

                // If we get to here then didn't succeed for reasons.
            }

            if ((component.Flags & PathFlags.Prying) != 0x0 && isDoor)
            {
                if (moving) // Frontier: settle before starting a pry that breaks on movement.
                    return SteeringObstacleStatus.Continuing;

                var doorQuery = GetEntityQuery<DoorComponent>();

                // Get the relevant obstacle
                foreach (var ent in obstacleEnts)
                {
                    if (doorQuery.TryGetComponent(ent, out var door) && door.State != DoorState.Open)
                    {
                        // TODO: Use the verb.

                        if (door.State != DoorState.Opening)
                            _pryingSystem.TryPry(ent, uid, out id, uid);

                        component.DoAfterId = id;
                        component.ObstacleActionTarget = id == null ? null : ent; // Frontier: bind the action to its blocker.
                        return SteeringObstacleStatus.Continuing;
                    }
                }

            }
            // Try climbing obstacles
            else if ((component.Flags & PathFlags.Climbing) != 0x0 && isClimbable)
            {
                if (moving) // Frontier: settle before starting a climb.
                    return SteeringObstacleStatus.Continuing;

                if (TryComp<ClimbingComponent>(uid, out var climbing))
                {
                    if (climbing.IsClimbing)
                    {
                        return SteeringObstacleStatus.Completed;
                    }
                    else if (climbing.NextTransition != null)
                    {
                        return SteeringObstacleStatus.Continuing;
                    }

                    var climbableQuery = GetEntityQuery<ClimbableComponent>();

                    // Get the relevant obstacle
                    foreach (var ent in obstacleEnts)
                    {
                        if (climbableQuery.TryGetComponent(ent, out var table) &&
                            _climb.CanVault(table, uid, uid, out _) &&
                            _climb.TryClimb(uid, uid, ent, out id, table, climbing))
                        {
                            component.DoAfterId = id;
                            component.ObstacleActionTarget = id == null ? null : ent; // Frontier: bind the action to its blocker.
                            return SteeringObstacleStatus.Continuing;
                        }
                    }
                }

            }
            // Frontier: wait through attack cooldowns, but fail if the obstacle cannot be damaged.
            else if ((component.Flags & PathFlags.Smashing) != 0x0)
            {
                return SmashObstacle(uid, obstacleEnts);
            }
            // End Frontier

            return SteeringObstacleStatus.Failed;
        }

        CancelObstacleAction(component); // Frontier: the blocker moved or disappeared.
        return SteeringObstacleStatus.Completed;
    }

    // Frontier: check current hard fixture collisions for both the free-space and action checks.
    internal EntityUid? FindObstacleEntities(EntityUid uid, PathPoly poly, List<EntityUid>? obstacles = null)
    {
        var candidates = _entSetPool.Get();
        try
        {
            // Inset the lookup so fixtures touching the edge of an adjacent node do not count.
            _lookup.GetLocalEntitiesIntersecting(poly.GraphUid, poly.Box.Enlarged(-0.04f), candidates,
                flags: LookupFlags.Dynamic | LookupFlags.Static);

            EntityUid? first = null;
            foreach (var ent in candidates)
            {
                if (ent == uid ||
                    !_fixturesQuery.TryGetComponent(ent, out var fixtures) ||
                    !PathfindingSystem.IsBodyRelevant(fixtures) ||
                    !_physics.IsCurrentlyHardCollidable(uid, (ent, fixtures, null)))
                {
                    continue;
                }

                if (obstacles == null)
                    return ent;

                obstacles.Add(ent);
                first ??= ent;
            }

            return first;
        }
        finally
        {
            _entSetPool.Return(candidates);
        }
    }

    private SteeringObstacleStatus SmashObstacle(EntityUid uid, List<EntityUid> obstacles)
    {
        if (!_melee.TryGetWeapon(uid, out var weapon, out var melee) ||
            !TryComp<CombatModeComponent>(uid, out var combatMode))
        {
            return SteeringObstacleStatus.Failed;
        }

        // Vary the first target where a grille and a window occupy the same tile.
        _random.Shuffle(obstacles);
        var needsApproach = false;
        foreach (var obstacle in obstacles)
        {
            if (!HasComp<DestructibleComponent>(obstacle) || !TryComp<DamageableComponent>(obstacle, out var damage))
                continue;

            // Pick a reachable fixture before swinging. The attack API can report a dispatched
            // swing even when the target is behind another blocker.
            if (!_interaction.InRangeUnobstructed(uid, obstacle, melee.Range))
            {
                needsApproach = true;
                continue;
            }

            if (melee.NextAttack > _timing.CurTime)
                return SteeringObstacleStatus.Continuing;

            var wasInCombat = combatMode.IsInCombatMode;
            var previousDamage = damage.TotalDamage;
            _combat.SetInCombatMode(uid, true, combatMode);
            var hit = _melee.AttemptLightAttack(uid, weapon, melee, obstacle);
            _combat.SetInCombatMode(uid, wasInCombat, combatMode);

            if (hit && (TerminatingOrDeleted(obstacle) || damage.TotalDamage > previousDamage))
                return SteeringObstacleStatus.Continuing;

            return SteeringObstacleStatus.Failed;
        }

        return needsApproach ? SteeringObstacleStatus.Approaching : SteeringObstacleStatus.Failed;
    }
    // End Frontier

    private enum SteeringObstacleStatus : byte
    {
        Completed,
        Failed,
        Continuing,
        Approaching // Frontier: stay on the current node and move into attack range.
    }
}
