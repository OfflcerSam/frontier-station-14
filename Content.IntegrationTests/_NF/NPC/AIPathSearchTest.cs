#nullable enable
using System.Linq;
using System.Threading;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared.NPC;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Random;

namespace Content.IntegrationTests._NF.NPC;

[TestFixture, Category("AILogicRegression")]
public sealed class AIPathSearchTest
{
    [Test]
    public async Task AStarKeepsTheSameRouteAndWorkAcrossSlices()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        await server.WaitPost(() => AddCorridor(server, map, 96));
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            var system = server.System<PathfindingSystem>();
            var start = map.GridCoords.Offset(new(0.5f, 0.5f));
            var end = start.Offset(new(95, 0));
            var first = NewAStar(start, end);
            var second = NewAStar(start, end);
            Assert.That(Finish(() => system.UpdateAStarPath(first, int.MaxValue)), Is.EqualTo(PathResult.Path));
            Assert.That(Finish(() => system.UpdateAStarPath(second, 1)), Is.EqualTo(PathResult.Path));
            Assert.That(second.Polys, Is.EqualTo(first.Polys));
            Assert.That(second.Expansions, Is.EqualTo(first.Expansions));
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(511, PathResult.Path, 512)]
    [TestCase(512, PathResult.BudgetExceeded, 512)]
    [TestCase(550, PathResult.NoPath, 512)]
    public async Task AStarSeparatesTheExactBudgetFromAProvenDeadEnd(int offset, PathResult expected, int expansions)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        await server.WaitPost(() =>
        {
            AddCorridor(server, map, offset == 550 ? 512 : 513);
            if (offset == 550)
                AddCorridor(server, map, 1, offset);
        });
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            var system = server.System<PathfindingSystem>();
            var start = map.GridCoords.Offset(new(0.5f, 0.5f));
            foreach (var slice in new[] { 19, int.MaxValue })
            {
                var search = NewAStar(start, start.Offset(new(offset, 0)));
                Assert.That(Finish(() => system.UpdateAStarPath(search, slice)), Is.EqualTo(expected));
                Assert.That(search.Expansions, Is.EqualTo(expansions));
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AStarUsesOneGoalSnapshotAcrossSlices()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        await server.WaitPost(() => AddCorridor(server, map, 16));
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            var system = server.System<PathfindingSystem>();
            var start = map.GridCoords.Offset(new(0.5f, 0.5f));
            var end = start.Offset(new(14, 0));
            var target = server.EntMan.SpawnEntity(null, end);
            var goal = new EntityCoordinates(target, 0f, 0f);
            var baseline = new AStarPathRequest(start, goal, PathFlags.None, 1.1f, 0, 0, CancellationToken.None);
            var sliced = new AStarPathRequest(start, goal, PathFlags.None, 1.1f, 0, 0, CancellationToken.None);
            Assert.That(Finish(() => system.UpdateAStarPath(baseline, int.MaxValue)), Is.EqualTo(PathResult.Path));
            Assert.That(system.UpdateAStarPath(sliced, 2), Is.EqualTo(PathResult.Continuing));
            server.System<SharedTransformSystem>().SetCoordinates(target, start.Offset(new(4, 0)));
            Assert.That(Finish(() => system.UpdateAStarPath(sliced, 2)), Is.EqualTo(PathResult.Path));
            Assert.That(sliced.Polys, Is.EqualTo(baseline.Polys));
            Assert.That(sliced.Expansions, Is.EqualTo(baseline.Expansions));
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(3f, 40, 4)]
    [TestCase(20f, 3, 3)]
    public async Task BfsBoundsDiscoveryAndTotalWorkAcrossSlices(float radius, int limit, int expansions)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        await server.WaitPost(() => AddCorridor(server, map, 32));
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            var system = server.System<PathfindingSystem>();
            var random = server.ResolveDependency<IRobustRandom>();
            var start = map.GridCoords.Offset(new(0.5f, 0.5f));
            BFSPathRequest? first = null;
            foreach (var slice in new[] { int.MaxValue, 2 })
            {
                random.SetSeed(211);
                var search = new BFSPathRequest(radius, limit, start, PathFlags.None, 0, 0, CancellationToken.None);
                Assert.That(Finish(() => system.UpdateBFSPath(random, search, slice)), Is.EqualTo(PathResult.Path));
                Assert.That(search.Expansions, Is.EqualTo(expansions));
                Assert.That(search.CostSoFar.Keys.All(poly => poly.Coordinates.Position.X - start.Position.X <= radius), Is.True);
                if (limit == 40)
                    Assert.That(search.CostSoFar.Keys.Any(poly => poly.Coordinates.Position.X - start.Position.X == radius), Is.True);
                if (first != null)
                    Assert.That(search.Polys, Is.EqualTo(first.Polys));
                first = search;
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExhaustedSearchCompletesThroughTheQueueAndNeverInstallsARoute()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        await server.WaitPost(() => AddCorridor(server, map, 520));
        await server.WaitRunTicks(30);
        using var cancellation = new CancellationTokenSource();
        Task<PathResultEvent> pending = default!;
        var start = map.GridCoords.Offset(new(0.5f, 0.5f));
        var goal = start.Offset(new(519, 0));
        await server.WaitPost(() => pending = server.System<PathfindingSystem>().GetPath(start, goal, 0, 0, 0, cancellation.Token));
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            Assert.That(pending.IsCompletedSuccessfully, Is.True);
            var result = pending.GetAwaiter().GetResult();
            Assert.That(result.Result, Is.EqualTo(PathResult.BudgetExceeded));
            Assert.That(result.Path, Is.Empty);

            var actor = server.EntMan.SpawnEntity("MobHumanPathDummy", start);
            var movement = server.System<NPCSteeringSystem>();
            var steering = movement.Register(actor, goal);
            steering.PathfindToken = cancellation;
            steering.FailedPathCount = NPCSteeringComponent.FailedPathLimit - 1;
            movement.CompletePathRequest(actor, steering, cancellation, goal, result);
            Assert.That(steering.PathfindToken, Is.Null);
            Assert.That(steering.CurrentPath, Is.Empty);
            Assert.That(steering.Status, Is.EqualTo(SteeringStatus.NoPath));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BfsUsesTheRequestedOriginEvenWhenTheSeedCenterIsOutsideTheRadius()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        await server.WaitPost(() => AddCorridor(server, map, 4));
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            var system = server.System<PathfindingSystem>();
            var random = server.ResolveDependency<IRobustRandom>();
            var start = map.GridCoords.Offset(new(0.1f, 0.5f));
            var request = new BFSPathRequest(0.2f, 40, start, PathFlags.None, 0, 0, CancellationToken.None);
            Assert.That(system.UpdateBFSPath(random, request, 2), Is.EqualTo(PathResult.NoPath));
            Assert.That(request.Polys, Is.Empty);
            Assert.That(request.Expansions, Is.EqualTo(1));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BfsChecksPortalEndpointsInMapSpace()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var start = map.GridCoords.Offset(new(0.5f, 0.5f));
        EntityCoordinates other = default;
        await server.WaitAssertion(() =>
        {
            var grid = server.MapMan.CreateGridEntity(map.MapId);
            server.System<SharedTransformSystem>().SetCoordinates(grid.Owner, new EntityCoordinates(map.MapUid, 10, 0));
            other = new EntityCoordinates(grid.Owner, 0.5f, 0.5f);
            var tile = new Tile(server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            server.System<SharedMapSystem>().SetTile(grid, other, tile);
            Assert.That(server.System<PathfindingSystem>().TryCreatePortal(start, other, out _), Is.True);
        });
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            var system = server.System<PathfindingSystem>();
            var random = server.ResolveDependency<IRobustRandom>();
            foreach (var radius in new[] { 9f, 10f })
            {
                var search = new BFSPathRequest(radius, 40, start, PathFlags.None, 0, 0, CancellationToken.None);
                Assert.That(Finish(() => system.UpdateBFSPath(random, search, 1)), Is.EqualTo(PathResult.Path));
                Assert.That(search.CostSoFar.Keys.Any(poly => poly.GraphUid == other.EntityId), Is.EqualTo(radius == 10f));
            }
        });
        await pair.CleanReturnAsync();
    }

    private static AStarPathRequest NewAStar(EntityCoordinates start, EntityCoordinates end)
        => new(start, end, PathFlags.None, 0, 0, 0, CancellationToken.None);

    private static PathResult Finish(Func<PathResult> step)
    {
        for (var i = 0; i < 600; i++)
        {
            var result = step();
            if (result != PathResult.Continuing)
                return result;
        }
        throw new AssertionException("Search made no bounded progress across forced slices.");
    }

    private static void AddCorridor(Robust.UnitTesting.RobustIntegrationTest.ServerIntegrationInstance server,
        Content.IntegrationTests.Pair.TestMapData map, int length, int offset = 0)
    {
        var tile = new Tile(server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
        var tiles = Enumerable.Range(offset, length).Select(x => (new Vector2i(x, 0), tile)).ToList();
        server.System<SharedMapSystem>().SetTiles(map.Grid.Owner, map.Grid.Comp, tiles);
    }
}
