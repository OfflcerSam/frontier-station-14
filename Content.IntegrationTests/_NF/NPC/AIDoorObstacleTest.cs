#nullable enable
using System.Linq;
using System.Threading;
using Content.Server.Access.Systems;
using Content.Server.Doors.Systems;
using Content.Server.Gravity;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared.Access.Components;
using Content.Shared.Damage;
using Content.Shared.DoAfter;
using Content.Shared.Doors.Components;
using Content.Shared.Gravity;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Components;
using Content.Shared.NPC;
using Content.Shared.Prying.Components;
using Content.Shared.Prying.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;
using Robust.UnitTesting;

namespace Content.IntegrationTests._NF.NPC;

[TestFixture, Category("AILogicRegression")]
public sealed class AIDoorObstacleTest
{
    [TestCase("missing capability")]
    [TestCase("disabled capability")]
    [TestCase("welded door")]
    [TestCase("non-pryable door")]
    public async Task RejectedPryFailsInsteadOfWaiting(string reason)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var system = server.System<NPCSteeringSystem>();
        var blocked = map.GridCoords.Offset(new(1.5f, 0.5f));
        EntityUid actor = default;
        await server.WaitPost(() =>
        {
            AddDoorTiles(server, map.Grid.Owner, map.Grid.Comp, blocked);
            var door = server.EntMan.SpawnEntity("Airlock", blocked);
            actor = server.EntMan.SpawnEntity("MobHumanPathDummy", blocked.Offset(new(-1, 0)));
            if (reason != "missing capability")
                server.EntMan.EnsureComponent<PryingComponent>(actor).Enabled = reason != "disabled capability";
            if (reason == "welded door")
                server.System<DoorSystem>().SetState(door, DoorState.Welded);
            if (reason == "non-pryable door")
                server.EntMan.GetComponent<DoorComponent>(door).CanPry = false;
        });
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            var steering = StartSteering(server, actor, blocked);
            system.Update(1f / 30);
            Assert.That(steering.DoAfterId, Is.Null);
            Assert.That(steering.Status, Is.EqualTo(SteeringStatus.NoPath));
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(true, true, true, true, DoorState.Opening, false)]
    [TestCase(true, false, true, false, DoorState.Closed, false)]
    [TestCase(true, true, false, false, DoorState.Closed, false)]
    [TestCase(true, false, true, true, DoorState.Closed, true)]
    [TestCase(false, true, true, false, DoorState.Closed, true)]
    public async Task SteeringChoosesDoorActionUsingLiveAccessAndPower(bool powered, bool hasAccess, bool canClick,
        bool pryPowered, DoorState expectedState, bool expectPry)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var system = server.System<NPCSteeringSystem>();
        var blocked = map.GridCoords.Offset(new(1.5f, 0.5f));
        var start = blocked.Offset(new(-1, 0));
        var goal = blocked.Offset(new(1, 0));
        EntityUid actor = default;
        EntityUid door = default;
        await server.WaitPost(() =>
        {
            AddDoorTiles(server, map.Grid.Owner, map.Grid.Comp, blocked);
            door = server.EntMan.SpawnEntity("AirlockEngineeringLocked", blocked);
            server.EntMan.GetComponent<DoorComponent>(door).BumpOpen = false;
            if (powered)
                server.EntMan.SpawnEntity("APCBasic", blocked);
            actor = server.EntMan.SpawnEntity("MobHumanPathDummy", start);
            if (!canClick)
                server.EntMan.RemoveComponent<ComplexInteractionComponent>(actor);
            server.EntMan.EnsureComponent<PryingComponent>(actor).PryPowered = pryPowered;
            if (hasAccess)
            {
                var access = server.EntMan.EnsureComponent<AccessComponent>(actor);
                server.System<AccessSystem>().TryAddGroups(actor, ["AllAccess"], access);
            }
        });
        await server.WaitRunTicks(30);
        Task<PathResultEvent> route = default!;
        await server.WaitPost(() =>
        {
            // A route through a reader-equipped door still uses live credentials before prying.
            route = server.System<PathfindingSystem>().GetPath(actor, start, goal, 0, CancellationToken.None,
                PathFlags.Interact | PathFlags.Prying);
        });
        await server.WaitRunTicks(30);
        var result = await route.WaitAsync(TimeSpan.FromSeconds(5));
        await server.WaitAssertion(() =>
        {
            Assert.That(result.Result, Is.EqualTo(PathResult.Path));
            Assert.That(server.EntMan.GetComponent<AirlockComponent>(door).Powered, Is.EqualTo(powered));
            Assert.That(server.System<DoorSystem>().HasAccess(door, actor), Is.EqualTo(hasAccess));
            var steering = StartSteering(server, actor, blocked, PathFlags.Interact | PathFlags.Prying);
            system.Update(1f / 30);
            Assert.That(server.EntMan.GetComponent<DoorComponent>(door).State, Is.EqualTo(expectedState));
            Assert.That(server.System<SharedDoAfterSystem>().IsRunning(steering.DoAfterId), Is.EqualTo(expectPry));
            if (!expectPry && expectedState == DoorState.Closed)
                Assert.That(steering.Status, Is.EqualTo(SteeringStatus.NoPath));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task InterruptedPryRetriesOnceAfterDelay()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var system = server.System<NPCSteeringSystem>();
        var doAfter = server.System<SharedDoAfterSystem>();
        var blocked = map.GridCoords.Offset(new(1.5f, 0.5f));
        EntityUid actor = default;
        NPCSteeringComponent steering = default!;
        DoAfterId? first = null;
        await server.WaitPost(() =>
        {
            AddDoorTiles(server, map.Grid.Owner, map.Grid.Comp, blocked);
            server.EntMan.SpawnEntity("Airlock", blocked);
            actor = server.EntMan.SpawnEntity("MobHumanPathDummy", blocked.Offset(new(-1, 0)));
            server.EntMan.EnsureComponent<PryingComponent>(actor);
        });
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            steering = StartSteering(server, actor, blocked);
            system.Update(1f / 30);
            first = steering.DoAfterId;
            Assert.That(doAfter.IsRunning(first), Is.True);
            doAfter.Cancel(first);
            system.Update(1f / 30);
            system.Update(1f / 30);
            Assert.That(steering.DoAfterId, Is.Null, "Cancellation should not cause an immediate restart.");
            Assert.That(steering.Status, Is.EqualTo(SteeringStatus.Moving));
        });
        await server.WaitRunTicks(45);
        await server.WaitAssertion(() =>
        {
            var retry = steering.DoAfterId;
            Assert.That(retry, Is.Not.EqualTo(first));
            Assert.That(doAfter.IsRunning(retry), Is.True);
            doAfter.Cancel(retry);
            system.Update(1f / 30);
            Assert.That(steering.DoAfterId, Is.Null);
            Assert.That(steering.Status, Is.EqualTo(SteeringStatus.NoPath));
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(true, DoorState.Open)]
    [TestCase(false, DoorState.Closing)]
    public async Task FinishingPryHonorsItsRequestedDoorState(bool openOnly, DoorState expectedState)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var blocked = map.GridCoords.Offset(new(1.5f, 0.5f));
        EntityUid door = default;
        DoAfterId? action = null;
        var doAfter = server.System<SharedDoAfterSystem>();
        await server.WaitAssertion(() =>
        {
            AddDoorTiles(server, map.Grid.Owner, map.Grid.Comp, blocked);
            door = server.EntMan.SpawnEntity("Airlock", blocked);
            var actor = server.EntMan.SpawnEntity("MobHumanPathDummy", blocked.Offset(new(-1, 0)));
            server.EntMan.EnsureComponent<PryingComponent>(actor);
            var pry = server.System<PryingSystem>();
            var started = openOnly
                ? pry.TryPryOpen(door, actor, out action)
                : pry.TryPry(door, actor, out action, actor);
            Assert.That(started, Is.True);
            Assert.That(doAfter.IsRunning(action), Is.True);
            server.System<DoorSystem>().StartOpening(door);
        });
        // This actor is not steering, so the original action reaches its normal completion callback.
        var status = DoAfterStatus.Running;
        for (var i = 0; i < 40 && status == DoAfterStatus.Running; i++)
        {
            await server.WaitRunTicks(5);
            await server.WaitPost(() => status = doAfter.GetStatus(action));
        }
        await server.WaitAssertion(() =>
        {
            Assert.That(status, Is.EqualTo(DoAfterStatus.Finished));
            Assert.That(server.EntMan.GetComponent<DoorComponent>(door).State, Is.EqualTo(expectedState));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PryApproachesWithinTheDoAfterRange()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var blocked = map.GridCoords.Offset(new(1.5f, 0.5f));
        var start = blocked.Offset(new(-1.75f, 0));
        EntityUid actor = default;
        EntityUid door = default;
        NPCSteeringComponent steering = default!;
        await server.WaitPost(() =>
        {
            AddDoorTiles(server, map.Grid.Owner, map.Grid.Comp, blocked);
            var tile = new Tile(server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            server.System<SharedMapSystem>().SetTile(map.Grid.Owner, map.Grid.Comp, start, tile);
            var gravity = server.EntMan.EnsureComponent<GravityComponent>(map.Grid);
            server.System<GravitySystem>().EnableGravity(map.Grid, gravity);
            door = server.EntMan.SpawnEntity("Airlock", blocked);
            actor = server.EntMan.SpawnEntity("MobHumanPathDummy", start);
            server.EntMan.EnsureComponent<PryingComponent>(actor);
        });
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            steering = StartSteering(server, actor, blocked);
            server.System<NPCSteeringSystem>().Update(1f / 30);
            Assert.That(steering.Status, Is.EqualTo(SteeringStatus.Moving));
            Assert.That(steering.DoAfterId, Is.Null);
        });
        await server.WaitRunTicks(120);
        await server.WaitAssertion(() =>
        {
            Assert.That(server.EntMan.GetComponent<DoorComponent>(door).State, Is.AnyOf(DoorState.Opening, DoorState.Open));
            Assert.That(steering.Status, Is.EqualTo(SteeringStatus.InRange));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RejectedPryCanFallBackToSmashing()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var blocked = map.GridCoords.Offset(new(1.5f, 0.5f));
        var start = blocked.Offset(new(-1, 0));
        EntityUid actor = default;
        EntityUid door = default;
        await server.WaitPost(() =>
        {
            AddDoorTiles(server, map.Grid.Owner, map.Grid.Comp, blocked);
            door = server.EntMan.SpawnEntity("Airlock", blocked);
            server.EntMan.GetComponent<DoorComponent>(door).CanPry = false;
            actor = server.EntMan.SpawnEntity("MobHumanPathDummy", start);
            server.EntMan.EnsureComponent<PryingComponent>(actor);
            var weapon = server.EntMan.SpawnEntity("Katana", start);
            Assert.That(server.System<SharedHandsSystem>().TryPickupAnyHand(actor, weapon), Is.True);
        });
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            var steering = StartSteering(server, actor, blocked, PathFlags.Prying | PathFlags.Smashing);
            server.System<NPCSteeringSystem>().Update(1f / 30);
            Assert.That(steering.DoAfterId, Is.Null);
            Assert.That(server.EntMan.GetComponent<DamageableComponent>(door).TotalDamage.Float(), Is.GreaterThan(0));
            Assert.That(steering.Status, Is.EqualTo(SteeringStatus.Moving));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AStuckActorSearchesForTheDoorEvenInsideDirectSteerRange()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var blocked = map.GridCoords.Offset(new(1.5f, 0.5f));
        var start = blocked.Offset(new(-0.8f, 0));
        var goal = blocked.Offset(new(0.6f, 0));
        await server.WaitPost(() =>
        {
            AddDoorTiles(server, map.Grid.Owner, map.Grid.Comp, blocked);
            server.EntMan.SpawnEntity("Airlock", blocked);
        });
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            var actor = server.EntMan.SpawnEntity("MobHumanPathDummy", start);
            var steeringSystem = server.System<NPCSteeringSystem>();
            var steering = steeringSystem.Register(actor, goal);
            server.EntMan.EnsureComponent<ActiveNPCComponent>(actor);
            steering.LastStuckTime = server.ResolveDependency<IGameTiming>().CurTime - TimeSpan.FromSeconds(2);
            steeringSystem.Update(1f / 30);
            Assert.That(steering.Pathfind, Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CrowdStartsOnlyOnePryForOneDoor()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var system = server.System<NPCSteeringSystem>();
        var blocked = map.GridCoords.Offset(new(1.5f, 0.5f));
        var actors = new EntityUid[12];
        await server.WaitPost(() => AddDoorTiles(server, map.Grid.Owner, map.Grid.Comp, blocked));
        await server.WaitPost(() => server.EntMan.SpawnEntity("Airlock", blocked));
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            // Spawn together immediately before steering so physics cannot push the crowd apart.
            for (var i = 0; i < actors.Length; i++)
            {
                actors[i] = server.EntMan.SpawnEntity("MobHumanPathDummy", blocked.Offset(new(-1, 0)));
                server.EntMan.EnsureComponent<PryingComponent>(actors[i]).PryPowered = true;
            }

            var owner = StartSteering(server, actors[0], blocked);
            system.Update(1f / 30);
            var action = owner.DoAfterId;
            var doAfter = server.System<SharedDoAfterSystem>();
            Assert.That(doAfter.GetStatus(action), Is.EqualTo(DoAfterStatus.Running));

            var followers = actors.Skip(1).Select(actor => StartSteering(server, actor, blocked)).ToArray();
            system.Update(1f / 30);
            Assert.That(followers.Count(steering => steering.DoAfterId != null), Is.Zero);
            Assert.That(followers.All(steering => steering.Status == SteeringStatus.Moving), Is.True);
            system.Unregister(actors[1]);
            Assert.That(doAfter.GetStatus(action), Is.EqualTo(DoAfterStatus.Running),
                "A follower must not own or cancel the first NPC's pry.");
        });
        await pair.CleanReturnAsync();
    }

    private static void AddDoorTiles(RobustIntegrationTest.IntegrationInstance server, EntityUid grid,
        MapGridComponent gridComponent, EntityCoordinates blocked)
    {
        var maps = server.System<SharedMapSystem>();
        var tile = new Tile(server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
        maps.SetTile(grid, gridComponent, blocked, tile);
        maps.SetTile(grid, gridComponent, blocked.Offset(new(1, 0)), tile);
    }

    private static NPCSteeringComponent StartSteering(RobustIntegrationTest.IntegrationInstance server,
        EntityUid actor, EntityCoordinates blocked, PathFlags flags = PathFlags.Prying)
    {
        var node = server.System<PathfindingSystem>().GetPoly(blocked);
        Assert.That(node, Is.Not.Null);
        var steering = server.System<NPCSteeringSystem>().Register(actor, blocked.Offset(new(1, 0)));
        steering.Flags = flags;
        steering.CurrentPath.Enqueue(node!);
        server.EntMan.EnsureComponent<ActiveNPCComponent>(actor);
        return steering;
    }
}
