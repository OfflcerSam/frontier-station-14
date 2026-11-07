using Content.Server.Destructible;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Climbing;
using Content.Shared.CombatMode;
using Content.Shared.Damage; // Frontier
using Content.Shared.DoAfter;
using Content.Shared.Doors.Components;
using Content.Shared.NPC;
using Content.Shared.Weapons.Melee; // Frontier
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
            // Finish or retire the current action before selecting another collider on this node.
            EntityUid? handledDoor = null;
            if (component.ObstacleActionTarget is { } target)
            {
                if (!obstacleEnts.Contains(target))
                {
                    CancelObstacleAction(component);
                }
                else if (TryComp<DoorComponent>(target, out var door))
                {
                    var status = TryHandleDoor(uid, component, target, door, moving);
                    if (status != SteeringObstacleStatus.Failed)
                        return status;
                    handledDoor = target;
                }
                else
                {
                    switch (_doAfter.GetStatus(component.DoAfterId))
                    {
                        case DoAfterStatus.Running:
                            return SteeringObstacleStatus.Continuing;
                        case DoAfterStatus.Cancelled:
                            return SteeringObstacleStatus.Failed;
                    }
                }
            }

            foreach (var ent in obstacleEnts)
            {
                if (ent == handledDoor || !TryComp<DoorComponent>(ent, out var door))
                    continue;

                var status = TryHandleDoor(uid, component, ent, door, moving);
                if (status != SteeringObstacleStatus.Failed)
                    return status;
            }

            // An empty locker can block the route to a visible target just like occupied storage.
            var storage = TryOpenRouteStorage(uid, component.Flags, obstacleEnts);
            if (storage != SteeringObstacleStatus.Failed)
                return storage;

            var id = component.DoAfterId;
            var isClimbable = obstacleEnts.Exists(ent => HasComp<ClimbableComponent>(ent));
            // End Frontier

            // Try climbing obstacles
            if ((component.Flags & PathFlags.Climbing) != 0x0 && isClimbable) // Frontier: try after door actions decline.
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
            if ((component.Flags & PathFlags.Smashing) != 0x0)
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

            return SmashReachableObstacle(uid, obstacle, weapon, melee, combatMode, damage);
        }

        return needsApproach ? SteeringObstacleStatus.Approaching : SteeringObstacleStatus.Failed;
    }

    internal SteeringObstacleStatus SmashObstacle(EntityUid uid, EntityUid obstacle)
    {
        if (!_melee.TryGetWeapon(uid, out var weapon, out var melee) ||
            !TryComp<CombatModeComponent>(uid, out var combatMode) ||
            !HasComp<DestructibleComponent>(obstacle) || !TryComp<DamageableComponent>(obstacle, out var damage))
        {
            return SteeringObstacleStatus.Failed;
        }

        if (!_interaction.InRangeUnobstructed(uid, obstacle, melee.Range))
            return SteeringObstacleStatus.Approaching;

        return SmashReachableObstacle(uid, obstacle, weapon, melee, combatMode, damage);
    }

    private SteeringObstacleStatus SmashReachableObstacle(EntityUid uid, EntityUid obstacle, EntityUid weapon,
        MeleeWeaponComponent melee, CombatModeComponent combatMode, DamageableComponent damage)
    {
        if (melee.NextAttack > _timing.CurTime)
            return SteeringObstacleStatus.Continuing;

        var wasInCombat = combatMode.IsInCombatMode;
        var previousDamage = damage.TotalDamage;
        _combat.SetInCombatMode(uid, true, combatMode);
        var hit = _melee.AttemptLightAttack(uid, weapon, melee, obstacle);
        _combat.SetInCombatMode(uid, wasInCombat, combatMode);

        return hit && (TerminatingOrDeleted(obstacle) || damage.TotalDamage > previousDamage)
            ? SteeringObstacleStatus.Continuing
            : SteeringObstacleStatus.Failed;
    }
    // End Frontier

    internal enum SteeringObstacleStatus : byte // Frontier: share obstacle attack outcomes with storage combat.
    {
        Completed,
        Failed,
        Continuing,
        // Frontier: keep the current node while approaching or working on a door.
        Approaching,
        DoorWorking,
        Waiting
        // End Frontier
    }
}
