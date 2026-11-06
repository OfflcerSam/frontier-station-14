using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Shared.ActionBlocker;
using Content.Shared.DoAfter;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Prying.Components;
using Content.Shared.Prying.Systems;
using Content.Shared.Tag;

namespace Content.Server.NPC.Systems;

public sealed partial class NPCSteeringSystem
{
    [Dependency] private readonly ActionBlockerSystem _doorActionBlocker = default!;
    [Dependency] private readonly SharedDoorSystem _doors = default!;
    [Dependency] private readonly TagSystem _doorTags = default!;

    private static readonly TimeSpan DoorRetryDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan DoorWaitLimit = TimeSpan.FromSeconds(3);

    // Only the steering component that started the pry owns its cancellation.
    private readonly Dictionary<EntityUid, DoAfterId> _doorPries = new();

    private void PruneDoorPries()
    {
        foreach (var (door, action) in _doorPries)
        {
            if (!_doAfter.IsRunning(action))
                _doorPries.Remove(door);
        }
    }

    private SteeringObstacleStatus TryHandleDoor(EntityUid uid, NPCSteeringComponent steering,
        EntityUid target, DoorComponent door, bool moving)
    {
        if (door.State is DoorState.Open or DoorState.Welded)
        {
            if (steering.ObstacleActionTarget == target)
                CancelObstacleAction(steering);
            return SteeringObstacleStatus.Failed;
        }

        // Let a door finish changing state before trying to act on it.
        if (door.State != DoorState.Closed)
            return SteeringObstacleStatus.Waiting;

        if (_doorPries.TryGetValue(target, out var pending) && _doAfter.IsRunning(pending))
            return SteeringObstacleStatus.DoorWorking;

        if (steering.ObstacleActionTarget == target && steering.DoAfterId != null)
        {
            CancelObstacleAction(steering);
            // Remember interruptions even if the actor was pushed out of pry range.
            steering.DoorRetryAt = steering.InterruptedDoor == target ? null : _timing.CurTime + DoorRetryDelay;
            steering.InterruptedDoor = target;
        }

        var retryExhausted = steering.InterruptedDoor == target && steering.DoorRetryAt == null;
        var canOpen = (steering.Flags & PathFlags.Interact) != 0 &&
            (door.ClickOpen && _doorActionBlocker.CanComplexInteract(uid) ||
             door.BumpOpen && _doorTags.HasTag(uid, SharedDoorSystem.DoorBumpTag));
        var canPry = !retryExhausted && (steering.Flags & PathFlags.Prying) != 0 && door.CanPry &&
            TryComp<PryingComponent>(uid, out var prying) && prying.Enabled;
        if ((!canOpen && !canPry) || !_doorActionBlocker.CanInteract(uid, target))
            return SteeringObstacleStatus.Failed;

        // Check the live door's power, bolts, and access with this actor as the user.
        if (canOpen)
        {
            if (!_interaction.InRangeAndAccessible(uid, target))
                return SteeringObstacleStatus.Approaching;

            if (_doors.TryOpen(target, door, uid, quiet: true) && door.State is DoorState.Open or DoorState.Opening)
                return SteeringObstacleStatus.DoorWorking;
        }

        if (!canPry)
            return SteeringObstacleStatus.Failed;

        if (!_interaction.InRangeAndAccessible(uid, target, PryingSystem.PryOpenRange))
            return SteeringObstacleStatus.Approaching;

        // Stop input while settling and while a cancelled pry is cooling down.
        if (moving || steering.InterruptedDoor == target && _timing.CurTime < steering.DoorRetryAt)
            return SteeringObstacleStatus.Waiting;

        if (!_pryingSystem.TryPryOpen(target, uid, out var action))
            return SteeringObstacleStatus.Failed;

        // An instant pry may already have opened the door by the time this returns.
        if (action is { } started && _doAfter.IsRunning(started))
        {
            steering.DoAfterId = started;
            steering.ObstacleActionTarget = target;
            _doorPries[target] = started;
            return SteeringObstacleStatus.DoorWorking;
        }

        return door.State is DoorState.Open or DoorState.Opening
            ? SteeringObstacleStatus.DoorWorking
            : SteeringObstacleStatus.Failed;
    }
}
