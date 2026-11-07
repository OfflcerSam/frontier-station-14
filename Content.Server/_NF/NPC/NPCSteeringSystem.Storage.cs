using Content.Server.NPC.Pathfinding;
using Content.Server.Storage.EntitySystems;
using Content.Shared.Hands.Components;
using Content.Shared.Lock;
using Content.Shared.Storage.Components;
using Content.Shared.Tools.Systems;

namespace Content.Server.NPC.Systems;

public sealed partial class NPCSteeringSystem
{
    [Dependency] private readonly EntityStorageSystem _routeStorage = default!;
    [Dependency] private readonly LockSystem _routeStorageLocks = default!;
    [Dependency] private readonly WeldableSystem _routeStorageWelds = default!;

    internal bool CanOperateStorage(EntityUid actor, PathFlags flags) =>
        (flags & PathFlags.Interact) != 0 && HasComp<HandsComponent>(actor) && _doorActionBlocker.CanComplexInteract(actor);

    private SteeringObstacleStatus TryOpenRouteStorage(EntityUid actor, PathFlags flags, List<EntityUid> obstacles)
    {
        if (!CanOperateStorage(actor, flags))
            return SteeringObstacleStatus.Failed;

        var needsApproach = false;
        foreach (var obstacle in obstacles)
        {
            if (!TryComp<EntityStorageComponent>(obstacle, out var storage) || storage.Open ||
                _routeStorageLocks.IsLocked(obstacle) || _routeStorageWelds.IsWelded(obstacle) ||
                !_doorActionBlocker.CanInteract(actor, obstacle))
            {
                continue;
            }

            if (!_interaction.InRangeAndAccessible(actor, obstacle))
            {
                needsApproach = true;
                continue;
            }

            // Opening changes collision. Recheck the whole node on the next update in case another blocker remains.
            if (_routeStorage.TryOpenStorage(actor, obstacle, silent: true))
                return SteeringObstacleStatus.Continuing;
        }

        return needsApproach ? SteeringObstacleStatus.Approaching : SteeringObstacleStatus.Failed;
    }
}
