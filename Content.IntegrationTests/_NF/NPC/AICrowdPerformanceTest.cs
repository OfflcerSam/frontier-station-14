#nullable enable
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Collections.Generic;
using Content.IntegrationTests.Pair;
using Content.Server.Atmos.Components;
using Content.Server.Body.Components;
using Content.Server.Gravity;
using Content.Server.NPC;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Server.Storage.EntitySystems;
using Content.Server.Temperature.Components;
using Content.Shared.CCVar;
using Content.Shared.Doors.Components;
using Content.Shared.Gravity;
using Content.Shared.NPC;
using Content.Shared.Prying.Components;
using Content.Shared.Storage.Components;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Profiling;
using Robust.Shared.Random;
using Robust.Shared.Threading;
using Robust.Shared.Timing;
using Robust.UnitTesting;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Content.IntegrationTests._NF.NPC;

[TestFixture, NonParallelizable, Category("AILogicCrowd")]
public sealed class AICrowdPerformanceTest
{
    public enum Scenario { Corridor, ContestedDoor, RejectedPry, Storage }

    private const int SampleTicks = 60;
    private const int Workers = 4;
    private const int DoorCohortSize = 16;
    private static readonly int[] Seeds = [1701, 1709, 1721, 1733, 1741];

    // Attach around the real server tick on the integration loop. This excludes harness messaging and observation work.
    private static readonly FieldInfo GameLoopField = typeof(RobustIntegrationTest.IntegrationInstance)
        .GetField("GameLoop", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo InitializeProfiler = typeof(ProfManager)
        .GetMethod("Initialize", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [TestPrototypes]
    private const string Prototypes = """
        - type: htnCompound
          id: NFCrowdPerfWait
          branches:
            - tasks:
                - !type:HTNPrimitiveTask
                  operator: !type:WaitOperator
                    key: CrowdWait
        """;

    public static IEnumerable<TestCaseData> Cases()
    {
        foreach (var scenario in Enum.GetValues<Scenario>())
        foreach (var actors in new[] { 1, 10, 50, 128, 256 })
        foreach (var seed in Seeds)
        {
            var test = new TestCaseData(scenario, actors, seed);
            if (seed == Seeds[0] && actors is 1 or 256)
                test.SetCategory("AILogicCrowdPilot");
            yield return test;
        }
    }

    [TestCaseSource(nameof(Cases))]
    public async Task ProfileRealTicks(Scenario scenario, int actors, int seed)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { ServerSeed = seed, ClientSeed = seed });
        // Warm a separate, equivalent workload before collecting a fresh run with fresh obstacles.
        var warm = await Run(pair, scenario, actors, seed, collect: false);
        var measured = await Run(pair, scenario, actors, seed, collect: true);
        var rows = measured.Samples;
        var name = $"crowd-{scenario}-{actors:D3}-{seed}";
        var folder = TestContext.CurrentContext.WorkDirectory;
        File.WriteAllLines(Path.Combine(folder, name + ".csv"),
            new[] { "tick,tick_ms,process_alloc_bytes,gc0,gc1,gc2,npc_ms,planner_ms,steering_ms,combat_ms,path_ms,navmesh_ms,expansions,path_completed,path_age_sum,path_age_max,htn_completed,htn_age_sum,htn_age_max,pry_attempts,pry_accepted,storage_attempts,storage_opened" }
                .Concat(rows.Select(row => row.ToCsv())));
        var result = FormattableString.Invariant(
            $"{scenario},{actors},{seed},{measured.Cap},{measured.Workers},{measured.TickRate},{measured.Reached},{measured.Failed},{measured.Opened},{measured.Obstacles},{warm.Reached},{warm.Failed},{warm.Opened}");
        File.WriteAllText(Path.Combine(folder, name + ".state.csv"),
            "scenario,actors,seed,cap,workers,tick_rate,reached,failed,opened,obstacles,warm_reached,warm_failed,warm_opened" + Environment.NewLine +
            result + Environment.NewLine);

        Assert.That(rows.Length, Is.EqualTo(SampleTicks));
        switch (scenario)
        {
            case Scenario.Corridor:
                Assert.That(measured.Reached, Is.GreaterThan(0), result);
                Assert.That(rows.Sum(row => row.Expansions), Is.GreaterThan(0), result);
                break;
            case Scenario.ContestedDoor:
                Assert.That(rows.Sum(row => row.PryAccepted), Is.EqualTo(measured.Obstacles), result);
                Assert.That(measured.Opened, Is.EqualTo(measured.Obstacles), result);
                break;
            case Scenario.RejectedPry:
                Assert.That(rows.Sum(row => row.PryAttempts), Is.GreaterThan(0), result);
                Assert.That(rows.Sum(row => row.PryAccepted), Is.Zero, result);
                Assert.That(measured.Opened, Is.Zero, result);
                Assert.That(measured.Failed, Is.EqualTo(actors), result);
                break;
            case Scenario.Storage:
                Assert.That(measured.Opened, Is.EqualTo(actors), result);
                break;
        }

        await pair.CleanReturnAsync();
    }

    private static async Task<Outcome> Run(TestPair pair, Scenario scenario, int count, int seed, bool collect)
    {
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var config = server.ResolveDependency<IConfigurationManager>();
        var previousCap = config.GetCVar(CCVars.NPCMaxUpdates);
        var previousThreads = config.GetCVar(CVars.ThreadParallelCount);
        var previousProfile = config.GetCVar(CVars.ProfEnabled);
        var timing = server.ResolveDependency<IGameTiming>();
        var profile = server.ResolveDependency<ProfManager>();
        var loop = GameLoopField.GetValue(server)!;
        var input = loop.GetType().GetEvent("Input")!;
        var tick = loop.GetType().GetEvent("Tick")!;
        var obstacles = new List<EntityUid>();
        var targets = new List<EntityUid>();
        var steering = new List<NPCSteeringComponent>();
        var samples = collect ? new Sample[SampleTicks] : [];
        var n = 0;
        var before = 0L;
        var allocated = 0L;
        var logStart = 0L;
        var gen0 = 0;
        var gen1 = 0;
        var gen2 = 0;
        var workers = 0;
        EventHandler<FrameEventArgs> onInput = (_, _) =>
        {
            logStart = profile.Buffer.LogWriteOffset;
            allocated = GC.GetTotalAllocatedBytes();
            gen0 = GC.CollectionCount(0);
            gen1 = GC.CollectionCount(1);
            gen2 = GC.CollectionCount(2);
            before = Stopwatch.GetTimestamp();
        };
        EventHandler<FrameEventArgs> onTick = (_, _) =>
        {
            var elapsed = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
            var row = new Sample
            {
                Tick = n + 1, TickMs = elapsed, Allocation = GC.GetTotalAllocatedBytes() - allocated,
                Gc0 = GC.CollectionCount(0) - gen0, Gc1 = GC.CollectionCount(1) - gen1, Gc2 = GC.CollectionCount(2) - gen2,
            };
            if (profile.Buffer.LogWriteOffset - logStart > profile.Buffer.LogBuffer.Length)
                throw new InvalidOperationException("The profiling ring wrapped inside one tick.");
            for (var index = logStart; index < profile.Buffer.LogWriteOffset; index++)
            {
                ref var entry = ref profile.Buffer.Log(index);
                if (entry.Type == ProfLogType.Value)
                    row.Add(profile.GetString(entry.Value.StringId), entry.Value.Value);
            }
            samples[n++] = row;
        };

        EntityCoordinates Lane(int lane) => map.GridCoords.Offset(new(0.5f, lane * 3 + 0.5f));

        var outcome = default(Outcome);
        try
        {
            await server.WaitAssertion(() =>
            {
                server.ResolveDependency<IRobustRandom>().SetSeed(seed);
                config.SetCVar(CCVars.NPCMaxUpdates, 128);
                config.SetCVar(CVars.ThreadParallelCount, Workers);
                Assert.That(config.GetCVar(CCVars.NPCMaxUpdates), Is.EqualTo(128));
                Assert.That(config.GetCVar(CVars.ThreadParallelCount), Is.EqualTo(Workers));
                workers = server.ResolveDependency<IParallelManager>().ParallelProcessCount;
                var gravity = server.EntMan.EnsureComponent<GravityComponent>(map.Grid);
                server.System<GravitySystem>().EnableGravity(map.Grid, gravity);
                var tile = new Tile(server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
                var lanes = scenario == Scenario.ContestedDoor ? (count + DoorCohortSize - 1) / DoorCohortSize : count;
                var length = scenario == Scenario.Corridor ? 8 : 4;
                var tiles = new List<(Vector2i, Tile)>(lanes * (length + 3));
                // A service strip joins the lanes so grid splitting does not detach their coordinates.
                for (var y = 0; y <= (lanes - 1) * 3; y++)
                    tiles.Add((new Vector2i(-1, y), tile));
                for (var lane = 0; lane < lanes; lane++)
                {
                    for (var x = 0; x < length; x++)
                        tiles.Add((new Vector2i(x, lane * 3), tile));
                }
                server.System<SharedMapSystem>().SetTiles(map.Grid.Owner, map.Grid.Comp, tiles);
                for (var i = 0; i < lanes; i++)
                {
                    var block = Lane(i).Offset(new(1, 0));
                    if (scenario is Scenario.ContestedDoor or Scenario.RejectedPry)
                    {
                        var door = server.EntMan.SpawnEntity("AirlockEngineeringLocked", block);
                        server.EntMan.GetComponent<DoorComponent>(door).BumpOpen = false;
                        obstacles.Add(door);
                        if (scenario == Scenario.RejectedPry)
                            server.EntMan.SpawnEntity("APCBasic", block);
                    }
                    else if (scenario == Scenario.Storage)
                    {
                        var storage = server.EntMan.SpawnEntity("CrateGenericSteel", block);
                        var hostile = SpawnHuman(server, block);
                        Assert.That(server.System<EntityStorageSystem>().Insert(hostile, storage), Is.True);
                        obstacles.Add(storage);
                        targets.Add(hostile);
                    }
                }
            });
            await server.WaitRunTicks(30);

            await server.WaitAssertion(() =>
            {
                var pathfinding = server.System<PathfindingSystem>();
                var movement = server.System<NPCSteeringSystem>();
                var doors = scenario is Scenario.ContestedDoor or Scenario.RejectedPry;
                for (var i = 0; i < count; i++)
                {
                    // Independent lanes isolate scaling work. A contested door shares its lane among a cohort.
                    var start = Lane(scenario == Scenario.ContestedDoor ? i / DoorCohortSize : i);
                    var actor = SpawnHuman(server, start);
                    var htn = server.EntMan.EnsureComponent<HTNComponent>(actor);
                    htn.RootTask = new HTNCompoundTask { Task = "NFCrowdPerfWait" };
                    htn.PlanCooldown = 0.5f;
                    htn.Blackboard.SetValue("CrowdWait", 60f);
                    if (doors)
                        htn.Blackboard.SetValue(NPCBlackboard.NavPry, true);
                    if (scenario == Scenario.Storage)
                        htn.Blackboard.SetValue(NPCBlackboard.NavInteract, true);
                    var goal = start.Offset(new(scenario == Scenario.Corridor ? 5 : 2, 0));
                    if (scenario == Scenario.Storage)
                    {
                        var combat = server.EntMan.EnsureComponent<NPCMeleeCombatComponent>(actor);
                        combat.Target = targets[i];
                        goal = new EntityCoordinates(targets[i], Vector2.Zero);
                    }

                    var move = movement.Register(actor, goal);
                    steering.Add(move);
                    if (scenario == Scenario.Storage)
                        move.Flags = PathFlags.Interact;
                    if (!doors)
                        continue;

                    move.Flags = PathFlags.Prying;
                    var prying = server.EntMan.EnsureComponent<PryingComponent>(actor);
                    prying.PryPowered = scenario == Scenario.ContestedDoor;
                    prying.SpeedModifier = 5f;
                    var poly = pathfinding.GetPoly(start.Offset(new(1, 0)));
                    Assert.That(poly, Is.Not.Null);
                    move.CurrentPath.Enqueue(poly!);
                }

                if (collect)
                {
                    // The headless server registers the engine profiler but only the client initializes it by default.
                    if (profile.Buffer.LogBuffer == null)
                        InitializeProfiler.Invoke(profile, null);
                    config.SetCVar(CVars.ProfEnabled, true);
                    Assert.That(profile.IsEnabled, Is.True);
                    input.AddEventHandler(loop, onInput);
                    tick.AddEventHandler(loop, onTick);
                }
            });
            await server.WaitRunTicks(SampleTicks);
            await server.WaitPost(() =>
            {
                var opened = scenario == Scenario.Storage
                    ? obstacles.Count(entity => server.EntMan.GetComponent<EntityStorageComponent>(entity).Open)
                    : obstacles.Count(entity => server.EntMan.GetComponent<DoorComponent>(entity).State is DoorState.Open or DoorState.Opening);
                outcome = new Outcome(samples, 128, workers, timing.TickRate,
                    steering.Count(move => move.Status == SteeringStatus.InRange),
                    steering.Count(move => move.Status == SteeringStatus.NoPath), opened, obstacles.Count);
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (collect)
                {
                    input.RemoveEventHandler(loop, onInput);
                    tick.RemoveEventHandler(loop, onTick);
                }
                config.SetCVar(CCVars.NPCMaxUpdates, previousCap);
                config.SetCVar(CVars.ThreadParallelCount, previousThreads);
                config.SetCVar(CVars.ProfEnabled, previousProfile);
                server.System<SharedMapSystem>().DeleteMap(map.MapId);
            });
        }
        Assert.That(n, Is.EqualTo(collect ? SampleTicks : 0));
        return outcome;
    }

    private static EntityUid SpawnHuman(RobustIntegrationTest.ServerIntegrationInstance server, EntityCoordinates at)
    {
        var human = server.EntMan.SpawnEntity("MobHumanPathDummy", at);
        server.EntMan.RemoveComponent<RespiratorComponent>(human);
        server.EntMan.RemoveComponent<TemperatureComponent>(human);
        server.EntMan.RemoveComponent<BarotraumaComponent>(human);
        return human;
    }

    private readonly record struct Outcome(Sample[] Samples, int Cap, int Workers, int TickRate, int Reached, int Failed,
        int Opened, int Obstacles);

    private struct Sample
    {
        public int Tick;
        public double TickMs, NpcMs, PlannerMs, SteeringMs, CombatMs, PathMs, NavmeshMs;
        public long Allocation;
        public int Gc0, Gc1, Gc2, Expansions, Paths, PathAgeSum, PathAgeMax, Plans, PlanAgeSum, PlanAgeMax;
        public int PryAttempts, PryAccepted, StorageAttempts, StorageOpened;

        public void Add(string name, ProfValue value)
        {
            switch (name)
            {
                case "NPCSystem": NpcMs += value.TimeAllocSample.Time * 1000; break;
                case "NPC HTN planner": PlannerMs += value.TimeAllocSample.Time * 1000; break;
                case "NPCSteeringSystem": SteeringMs += value.TimeAllocSample.Time * 1000; break;
                case "NPCCombatSystem": CombatMs += value.TimeAllocSample.Time * 1000; break;
                case "PathfindingSystem": PathMs += value.TimeAllocSample.Time * 1000; break;
                case "NPC navmesh rebuild": NavmeshMs += value.TimeAllocSample.Time * 1000; break;
                case "NPC path expansions": Expansions += value.Int32; break;
                case "NPC path completion age": Paths++; PathAgeSum += value.Int32; PathAgeMax = Math.Max(PathAgeMax, value.Int32); break;
                case "NPC HTN completion age": Plans++; PlanAgeSum += value.Int32; PlanAgeMax = Math.Max(PlanAgeMax, value.Int32); break;
                case "NPC pry accepted": PryAttempts++; PryAccepted += value.Int32; break;
                case "NPC storage opened": StorageAttempts++; StorageOpened += value.Int32; break;
            }
        }

        public readonly string ToCsv() => string.Create(CultureInfo.InvariantCulture,
            $"{Tick},{TickMs:F5},{Allocation},{Gc0},{Gc1},{Gc2},{NpcMs:F5},{PlannerMs:F5},{SteeringMs:F5},{CombatMs:F5},{PathMs:F5},{NavmeshMs:F5},{Expansions},{Paths},{PathAgeSum},{PathAgeMax},{Plans},{PlanAgeSum},{PlanAgeMax},{PryAttempts},{PryAccepted},{StorageAttempts},{StorageOpened}");
    }
}
