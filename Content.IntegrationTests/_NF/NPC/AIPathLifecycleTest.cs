#nullable enable
using System;
using System.Linq;
using System.Threading;
using Content.Server.Doors.Systems;
using Content.Server.Gravity;
using Content.Server.NPC;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN.PrimitiveTasks.Operators;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared.DoAfter;
using Content.Shared.Doors.Components;
using Content.Shared.Gravity;
using Content.Shared.NPC;
using Content.Shared.Prying.Components;
using Content.Shared.Prying.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests._NF.NPC;

[TestFixture]
[Category("AILogicRegression")]
public sealed class AIPathLifecycleTest
{
    [Test]
    public async Task PreCancelledRequestReturnsWithoutWaitingForATick()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await pair.Server.WaitAssertion(() =>
        {
            var pathfinding = pair.Server.System<PathfindingSystem>();
            var request = pathfinding.GetPath(map.GridCoords, map.GridCoords, 0, 0, 0, cancel.Token);
            Assert.That(request.IsCompletedSuccessfully, Is.True, "A pre-cancelled request should never enter the queue.");
            Assert.That(request.GetAwaiter().GetResult().Result, Is.EqualTo(PathResult.NoPath));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CancelledRequestsBeyondTheTickLimitAreDrained()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        using var cancel = new CancellationTokenSource();
        var requests = new Task<PathResultEvent>[600];
        Task<PathResultEvent> live = default!;

        await pair.Server.WaitAssertion(() =>
        {
            var pathfinding = pair.Server.System<PathfindingSystem>();
            for (var i = 0; i < requests.Length; i++)
                requests[i] = pathfinding.GetPath(map.GridCoords, map.GridCoords, 0, 0, 0, cancel.Token);

            live = pathfinding.GetPath(map.GridCoords, map.GridCoords, 0, 0, 0, CancellationToken.None);
            Assert.That(requests.All(request => !request.IsCompleted), Is.True);
            cancel.Cancel();
        });

        // Two ticks allow the live request to finish but cannot process 600 searches at 256 per tick.
        await pair.Server.WaitRunTicks(2);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(requests.All(request => request.IsCompletedSuccessfully &&
                request.GetAwaiter().GetResult().Result == PathResult.NoPath), Is.True);
            Assert.That(live.IsCompletedSuccessfully, Is.True);
            Assert.That(live.GetAwaiter().GetResult().Result, Is.EqualTo(PathResult.Path));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ChangingGoalResetsFailureButRegisteringSameGoalDoesNot()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var actor = server.EntMan.SpawnEntity("MobHumanPathDummy", map.GridCoords);
            var system = server.System<NPCSteeringSystem>();
            var first = map.GridCoords.Offset(new(3, 0));
            var second = map.GridCoords.Offset(new(0, 3));
            var steering = system.Register(actor, first);
            steering.Status = SteeringStatus.NoPath;
            steering.FailedPathCount = NPCSteeringComponent.FailedPathLimit;

            system.Register(actor, first);
            Assert.That(steering.Status, Is.EqualTo(SteeringStatus.NoPath));
            Assert.That(steering.FailedPathCount, Is.EqualTo(NPCSteeringComponent.FailedPathLimit));

            system.Register(actor, second);
            Assert.That(steering.Status, Is.EqualTo(SteeringStatus.Moving));
            Assert.That(steering.FailedPathCount, Is.Zero);
            Assert.That(steering.Coordinates, Is.EqualTo(second));
        });

        await pair.CleanReturnAsync();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ObsoleteResultCannotTouchNewRequest(bool obsoleteCompletesFirst)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        await server.WaitRunTicks(30);

        using var oldRequest = new CancellationTokenSource();
        using var newRequest = new CancellationTokenSource();

        await server.WaitAssertion(() =>
        {
            var actor = server.EntMan.SpawnEntity("MobHumanPathDummy", map.GridCoords);
            var system = server.System<NPCSteeringSystem>();
            var first = map.GridCoords.Offset(new(3, 0));
            var second = map.GridCoords.Offset(new(0.5f, 0.5f));
            var poly = server.System<PathfindingSystem>().GetPoly(second);
            Assert.That(poly, Is.Not.Null);
            var steering = system.Register(actor, first);
            steering.PathfindToken = oldRequest;

            system.Register(actor, second);
            Assert.That(oldRequest.IsCancellationRequested, Is.True);
            steering.PathfindToken = newRequest;
            steering.FailedPathCount = 2;

            var obsolete = new PathResultEvent(PathResult.NoPath, []);
            if (obsoleteCompletesFirst)
            {
                system.CompletePathRequest(actor, steering, oldRequest, first, obsolete);
                Assert.That(steering.PathfindToken, Is.SameAs(newRequest));
                Assert.That(steering.FailedPathCount, Is.EqualTo(2));
                Assert.That(steering.Status, Is.EqualTo(SteeringStatus.Moving));
            }

            system.CompletePathRequest(actor, steering, newRequest, second, new PathResultEvent(PathResult.Path, [poly!]));

            if (!obsoleteCompletesFirst)
                system.CompletePathRequest(actor, steering, oldRequest, first, obsolete);

            Assert.That(steering.PathfindToken, Is.Null);
            Assert.That(steering.CurrentPath.Single(), Is.SameAs(poly));
            Assert.That(steering.FailedPathCount, Is.Zero, "A successful path should reset consecutive failures.");
            Assert.That(steering.Status, Is.EqualTo(SteeringStatus.Moving));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RemovingSteeringCancelsItsRequestAndRejectsItsResult()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        using var request = new CancellationTokenSource();

        await server.WaitAssertion(() =>
        {
            var actor = server.EntMan.SpawnEntity("MobHumanPathDummy", map.GridCoords);
            var system = server.System<NPCSteeringSystem>();
            var destination = map.GridCoords.Offset(new(3, 0));
            var oldSteering = system.Register(actor, destination);
            oldSteering.PathfindToken = request;

            system.Unregister(actor);
            var newSteering = system.Register(actor, destination);
            Assert.That(newSteering, Is.Not.SameAs(oldSteering));
            Assert.That(request.IsCancellationRequested, Is.True);

            system.CompletePathRequest(actor, oldSteering, request, destination, new PathResultEvent(PathResult.NoPath, []));
            Assert.That(oldSteering.FailedPathCount, Is.Zero);
            Assert.That(newSteering.FailedPathCount, Is.Zero);
            Assert.That(newSteering.Status, Is.EqualTo(SteeringStatus.Moving));
        });

        await pair.CleanReturnAsync();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task MovementPlanReplacesPendingSearchForSameGoal(bool directMove)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        await server.WaitRunTicks(30);
        using var request = new CancellationTokenSource();

        await server.WaitAssertion(() =>
        {
            var actor = server.EntMan.SpawnEntity("MobHumanPathDummy", map.GridCoords);
            var system = server.System<NPCSteeringSystem>();
            var destination = map.GridCoords.Offset(new(0.5f, 0.5f));
            var poly = server.System<PathfindingSystem>().GetPoly(destination);
            Assert.That(poly, Is.Not.Null);
            var steering = system.Register(actor, destination);
            steering.FailedPathCount = 2;
            steering.PathfindToken = request;
            steering.CurrentPath.Enqueue(poly!);
            var now = server.ResolveDependency<IGameTiming>().CurTime;
            steering.LastStuckTime = now - TimeSpan.FromSeconds(10);

            var move = new MoveToOperator();
            move.Initialize(server.EntMan.EntitySysManager);
            var blackboard = new NPCBlackboard();
            blackboard.SetValue(NPCBlackboard.Owner, actor);
            blackboard.SetValue(move.TargetKey, destination);
            if (directMove)
                blackboard.SetValue(move.DirectMoveTargetKey, true);
            else
                blackboard.SetValue(move.PathfindKey, new PathResultEvent(PathResult.Path, [poly!]));

            move.Startup(blackboard);
            Assert.That(request.IsCancellationRequested, Is.True);
            Assert.That(steering.PathfindToken, Is.Null);
            Assert.That(steering.DirectMove, Is.EqualTo(directMove));
            Assert.That(steering.FailedPathCount, Is.Zero);
            Assert.That(steering.LastStuckTime, Is.EqualTo(now), "A replacement plan needs a fresh stuck timeout.");

            system.CompletePathRequest(actor, steering, request, destination, new PathResultEvent(PathResult.NoPath, []));
            Assert.That(steering.CurrentPath.Count, Is.EqualTo(directMove ? 0 : 1));
            Assert.That(steering.FailedPathCount, Is.Zero);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DeletedDestinationDoesNotCountAsAPathFailure()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        using var request = new CancellationTokenSource();

        await server.WaitAssertion(() =>
        {
            var actor = server.EntMan.SpawnEntity("MobHumanPathDummy", map.GridCoords);
            var target = server.EntMan.SpawnEntity(null, map.GridCoords.Offset(new(3, 0)));
            var system = server.System<NPCSteeringSystem>();
            var destination = new EntityCoordinates(target, 0, 0);
            var steering = system.Register(actor, destination);
            steering.PathfindToken = request;

            server.EntMan.DeleteEntity(target);
            system.CompletePathRequest(actor, steering, request, destination, new PathResultEvent(PathResult.NoPath, []));
            Assert.That(steering.FailedPathCount, Is.Zero);
            Assert.That(steering.PathfindToken, Is.Null);
        });

        await pair.CleanReturnAsync();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ChangingOrRemovingGoalCancelsPendingPry(bool retarget)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var actor = server.EntMan.SpawnEntity("MobHumanPathDummy", map.GridCoords.Offset(new(0.5f, 0.5f)));
            var door = server.EntMan.SpawnEntity("Airlock", map.GridCoords.Offset(new(1.5f, 0.5f)));
            server.EntMan.EnsureComponent<PryingComponent>(actor).PryPowered = true;
            var system = server.System<NPCSteeringSystem>();
            var steering = system.Register(actor, map.GridCoords.Offset(new(3, 0)));
            var doAfter = server.System<SharedDoAfterSystem>();

            Assert.That(server.System<PryingSystem>().TryPry(door, actor, out var action, actor), Is.True);
            Assert.That(doAfter.GetStatus(action), Is.EqualTo(DoAfterStatus.Running));
            steering.DoAfterId = action;

            if (retarget)
                system.Register(actor, map.GridCoords.Offset(new(0, 3)));
            else
                system.Unregister(actor);

            Assert.That(steering.DoAfterId, Is.Null);
            Assert.That(doAfter.GetStatus(action), Is.EqualTo(DoAfterStatus.Cancelled));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task OpeningDoorInvalidatesRouteAndSteeringFindsAReplacement()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var start = map.GridCoords.Offset(new(0.5f, 0.5f));
        var doorPosition = map.GridCoords.Offset(new(2.5f, 0.5f));
        var destination = map.GridCoords.Offset(new(4.5f, 0.5f));
        EntityUid actor = default;
        EntityUid door = default;
        PathPoly originalNode = default!;
        NPCSteeringComponent steering = default!;

        await server.WaitPost(() =>
        {
            var tiles = server.ResolveDependency<ITileDefinitionManager>();
            var tile = new Tile(tiles["Plating"].TileId);
            var maps = server.System<SharedMapSystem>();
            for (var x = 1; x <= 4; x++)
                maps.SetTile(map.Grid.Owner, map.Grid.Comp, map.GridCoords.Offset(new(x, 0)), tile);

            var gravity = server.EntMan.EnsureComponent<GravityComponent>(map.Grid);
            server.System<GravitySystem>().EnableGravity(map.Grid, gravity);
            actor = server.EntMan.SpawnEntity("MobHumanPathDummy", start);
            door = server.EntMan.SpawnEntity("Airlock", doorPosition);
            var airlock = server.EntMan.GetComponent<AirlockComponent>(door);
            server.System<AirlockSystem>().SetAutoCloseDelayModifier(airlock, 4);
            steering = server.System<NPCSteeringSystem>().Register(actor, destination);
        });
        await server.WaitRunTicks(30);

        await server.WaitAssertion(() =>
        {
            originalNode = server.System<PathfindingSystem>().GetPoly(doorPosition)!;
            Assert.That(originalNode, Is.Not.Null);
            Assert.That(originalNode.Data.Flags.HasFlag(PathfindingBreadcrumbFlag.Door), Is.True);
            steering.CurrentPath.Enqueue(originalNode);
            server.System<DoorSystem>().StartOpening(door);
        });
        await server.WaitRunTicks(45);

        await server.WaitAssertion(() =>
        {
            Assert.That(originalNode.IsValid(), Is.False, "Opening must invalidate the closed door polygon.");
            server.EntMan.EnsureComponent<ActiveNPCComponent>(actor);
        });
        await server.WaitRunTicks(4);

        await server.WaitAssertion(() =>
        {
            Assert.That(steering.Status, Is.EqualTo(SteeringStatus.Moving));
            Assert.That(steering.CurrentPath, Is.Not.Empty);
            Assert.That(steering.CurrentPath.All(node => node.IsValid()), Is.True);
            Assert.That(steering.CurrentPath.Any(node => ReferenceEquals(node, originalNode)), Is.False);
        });

        var arrived = false;
        for (var tick = 0; tick < 120 && !arrived; tick++)
        {
            await server.WaitRunTicks(1);
            await server.WaitPost(() => arrived = steering.Status == SteeringStatus.InRange);
        }

        Assert.That(arrived, Is.True, "The NPC should reach the destination through the opened door.");
        await pair.CleanReturnAsync();
    }
}
