#nullable enable
using System.Linq;
using System.Numerics;
using System.Threading;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Content.Server.NPC.HTN.PrimitiveTasks.Operators;
using Content.Server.NPC.Queries;
using Content.Server.NPC.Systems;
using Content.Server.Worldgen.Components;
using Content.Shared.CCVar;
using Robust.Shared.CPUJob.JobQueues;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.UnitTesting;

namespace Content.IntegrationTests._NF.NPC;

[TestFixture, Category("AILogicRegression")]
public sealed class AISchedulerTest
{
    private const string WaitKey = "SchedulerTestWait";

    [TestPrototypes]
    private const string Prototypes = """
        - type: htnCompound
          id: NFSchedulerRankingRoot
          branches:
            - preconditions:
                - !type:KeyExistsPrecondition
                  key: UseHighBranch
              tasks:
                - !type:HTNCompoundTask
                  task: NFSchedulerRankingHigh
            - tasks:
                - !type:HTNCompoundTask
                  task: NFSchedulerRankingLow

        - type: htnCompound
          id: NFSchedulerRankingHigh
          branches:
            - preconditions:
                - !type:KeyExistsPrecondition
                  key: UseFirstLeaf
              tasks:
                - !type:HTNPrimitiveTask
                  operator: !type:WaitOperator
                    key: SchedulerRankingWait
            - tasks:
                - !type:HTNPrimitiveTask
                  operator: !type:WaitOperator
                    key: SchedulerRankingWait

        - type: htnCompound
          id: NFSchedulerRankingLow
          branches:
            - tasks:
                - !type:HTNPrimitiveTask
                  operator: !type:WaitOperator
                    key: SchedulerRankingWait

        - type: utilityQuery
          id: NFSchedulerEmptyQuery
        """;

    [Test]
    public async Task DisabledEntriesDoNotChangeWhoGetsTheNextTurn()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var config = server.ResolveDependency<IConfigurationManager>();
        var previousCap = config.GetCVar(CCVars.NPCMaxUpdates);
        HTNComponent[] active = [];
        HTNComponent excluded = default!;
        HTNComponent unplanned = default!;
        WorldControllerComponent world = default!;
        using var cancellation = new CancellationTokenSource();
        try
        {
            await server.WaitAssertion(() =>
            {
                config.SetCVar(CCVars.NPCMaxUpdates, 2);
                Assert.That(config.GetCVar(CCVars.NPCMaxUpdates), Is.EqualTo(2));
                for (var i = 0; i < 2; i++)
                    SpawnWaiting(server, map.GridCoords, 100f).Enabled = false;

                var unloaded = server.System<SharedMapSystem>().CreateMap(out var unloadedId);
                world = server.EntMan.EnsureComponent<WorldControllerComponent>(unloaded);
                var inactive = server.EntMan.SpawnEntity(null, new MapCoordinates(Vector2.Zero, unloadedId));
                excluded = InstallWait(server, inactive, 100f);
                unplanned = SpawnWaiting(server, map.GridCoords, 100f);
                unplanned.Plan = null;
                HoldReplacement(server, unplanned, cancellation);
                active = Enumerable.Range(0, 4).Select(_ => SpawnWaiting(server, map.GridCoords, 100f)).ToArray();
            });
            await server.WaitRunTicks(1);
            await server.WaitAssertion(() =>
            {
                Assert.That(active.Count(comp => comp.Blackboard.GetValue<float>(WaitKey) < 100f), Is.EqualTo(2));
            });
            await server.WaitRunTicks(1);
            await server.WaitAssertion(() =>
            {
                Assert.That(active.Count(comp => comp.Blackboard.GetValue<float>(WaitKey) < 100f), Is.EqualTo(4));
                Assert.That(excluded.Blackboard.GetValue<float>(WaitKey), Is.EqualTo(100f));
                Assert.That(unplanned.Blackboard.GetValue<float>(WaitKey), Is.EqualTo(100f));
                Assert.That(world.Chunks, Is.Empty, "Eligibility checks must not generate world chunks.");
            });
        }
        finally
        {
            await server.WaitPost(() => config.SetCVar(CCVars.NPCMaxUpdates, previousCap));
        }
        await pair.CleanReturnAsync();
    }

    [TestCase(256)]
    [TestCase(512)]
    public async Task AOneSecondWaitUsesSimulationTimeUnderTheProductionCap(int actors)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var config = server.ResolveDependency<IConfigurationManager>();
        var previousCap = config.GetCVar(CCVars.NPCMaxUpdates);
        var waiters = new HTNComponent[actors];
        var lastValues = Enumerable.Repeat(1f, actors).ToArray();
        var lastTurns = new int[actors];
        var maxGap = 0;
        var ticks = 0;
        try
        {
            await server.WaitAssertion(() =>
            {
                config.SetCVar(CCVars.NPCMaxUpdates, 128);
                Assert.That(config.GetCVar(CCVars.NPCMaxUpdates), Is.EqualTo(128));
                ticks = server.ResolveDependency<IGameTiming>().TickRate + actors / 128 + 2;
                for (var i = 0; i < actors; i++)
                    waiters[i] = SpawnWaiting(server, map.GridCoords, 1f);
            });
            for (var tick = 1; tick <= ticks; tick++)
            {
                await server.WaitRunTicks(1);
                await server.WaitAssertion(() =>
                {
                    var updated = 0;
                    for (var i = 0; i < waiters.Length; i++)
                    {
                        var remaining = waiters[i].Blackboard.ContainsKey(WaitKey)
                            ? waiters[i].Blackboard.GetValue<float>(WaitKey)
                            : -1f;
                        if (remaining == lastValues[i])
                            continue;

                        updated++;
                        maxGap = Math.Max(maxGap, tick - lastTurns[i]);
                        lastTurns[i] = tick;
                        lastValues[i] = remaining;
                    }
                    Assert.That(updated, Is.LessThanOrEqualTo(128));
                });
            }
            await server.WaitAssertion(() =>
            {
                Assert.That(waiters.Count(comp => comp.Blackboard.ContainsKey(WaitKey)), Is.Zero,
                    $"Every one-second wait must finish within {ticks} simulation ticks.");
                Assert.That(maxGap, Is.LessThanOrEqualTo(actors / 128));
            });
            TestContext.WriteLine($"{actors} actors at cap 128, maximum interval {maxGap} simulation ticks; " +
                                  $"last one-second wait completed on tick {lastTurns.Max()}.");
        }
        finally
        {
            await server.WaitPost(() => config.SetCVar(CCVars.NPCMaxUpdates, previousCap));
        }
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PendingReplacementDoesNotFreezeTheCurrentWait()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        using var cancellation = new CancellationTokenSource();
        HTNComponent npc = default!;
        HTNPlanJob held = default!;
        await server.WaitAssertion(() =>
        {
            npc = SpawnWaiting(server, map.GridCoords, 1f);
            held = HoldReplacement(server, npc, cancellation);
        });
        await server.WaitRunTicks(5);
        await server.WaitAssertion(() =>
        {
            Assert.That(npc.Blackboard.GetValue<float>(WaitKey), Is.LessThan(1f));
            Assert.That(npc.PlanningJob, Is.SameAs(held));
            npc.Blackboard.SetValue(WaitKey, 0f);
        });
        await server.WaitRunTicks(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(npc.Plan, Is.Null);
            Assert.That(npc.PlanningJob, Is.Null);
            Assert.That(cancellation.IsCancellationRequested, Is.True);
            held.Run();
            Assert.That(held.Status, Is.EqualTo(JobStatus.Finished));
            Assert.That(held.Result, Is.Null);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RemovalAndInsertionPreserveTheRemainingRotation()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var config = server.ResolveDependency<IConfigurationManager>();
        var previousCap = config.GetCVar(CCVars.NPCMaxUpdates);
        HTNComponent[] npcs = [];
        HTNComponent added = default!;
        try
        {
            await server.WaitPost(() =>
            {
                config.SetCVar(CCVars.NPCMaxUpdates, 2);
                npcs = Enumerable.Range(0, 4).Select(_ => SpawnWaiting(server, map.GridCoords, 100f)).ToArray();
            });
            await server.WaitRunTicks(1);
            await server.WaitAssertion(() =>
            {
                Assert.That(npcs.Take(2).All(comp => comp.Blackboard.GetValue<float>(WaitKey) < 100f), Is.True);
                server.EntMan.DeleteEntity(npcs[0].Owner);
                added = SpawnWaiting(server, map.GridCoords, 100f);
            });
            await server.WaitRunTicks(1);
            await server.WaitAssertion(() =>
            {
                Assert.That(npcs.Skip(2).All(comp => comp.Blackboard.GetValue<float>(WaitKey) < 100f), Is.True);
                Assert.That(added.Blackboard.GetValue<float>(WaitKey), Is.EqualTo(100f));
            });
            await server.WaitRunTicks(1);
            await server.WaitAssertion(() => Assert.That(added.Blackboard.GetValue<float>(WaitKey), Is.LessThan(100f)));
        }
        finally
        {
            await server.WaitPost(() => config.SetCVar(CCVars.NPCMaxUpdates, previousCap));
        }
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ANewTaskDoesNotConsumeItsPredecessorsTime()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        const string nextKey = "SchedulerTestNextWait";
        HTNComponent npc = default!;
        await server.WaitPost(() =>
        {
            npc = SpawnWaiting(server, map.GridCoords, 0f);
            var next = new WaitOperator { Key = nextKey };
            next.Initialize(server.EntMan.EntitySysManager);
            npc.Blackboard.SetValue(nextKey, 1f);
            npc.Plan = new HTNPlan([npc.Plan!.CurrentTask, new HTNPrimitiveTask { Operator = next }], [0], [null, null]);
        });
        await server.WaitRunTicks(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(npc.Plan?.Index, Is.EqualTo(1));
            Assert.That(npc.Blackboard.GetValue<float>(nextKey), Is.EqualTo(1f));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task UtilityServicesUseIndependentIntervalsDuringAReplacement()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        using var cancellation = new CancellationTokenSource();
        const string fast = "SchedulerFastService";
        const string slow = "SchedulerSlowService";
        HTNComponent npc = default!;
        HTNPlan original = default!;
        var tickRate = 0;
        await server.WaitPost(() =>
        {
            tickRate = server.ResolveDependency<IGameTiming>().TickRate;
            npc = SpawnWaiting(server, map.GridCoords, 10f);
            original = npc.Plan!;
            original.CurrentTask.Services.Add(new UtilityService
            {
                ID = "SchedulerFast", Prototype = "NFSchedulerEmptyQuery", Key = fast, MinCooldown = 0.2f, MaxCooldown = 0.2f
            });
            original.CurrentTask.Services.Add(new UtilityService
            {
                ID = "SchedulerSlow", Prototype = "NFSchedulerEmptyQuery", Key = slow, MinCooldown = 0.5f, MaxCooldown = 0.5f
            });
            HoldReplacement(server, npc, cancellation);
        });
        await server.WaitRunTicks(Math.Max(1, tickRate / 10));
        await server.WaitAssertion(() =>
        {
            Assert.That(npc.Blackboard.ContainsKey(fast), Is.False);
            Assert.That(npc.Blackboard.ContainsKey(slow), Is.False);
            Assert.That(npc.PlanningJob, Is.Not.Null);
        });
        await server.WaitRunTicks(tickRate / 5 + 1);
        await server.WaitAssertion(() =>
        {
            Assert.That(npc.Blackboard.GetValue<EntityUid>(fast), Is.EqualTo(EntityUid.Invalid));
            Assert.That(npc.Blackboard.ContainsKey(slow), Is.False);
            Assert.That(cancellation.IsCancellationRequested, Is.True);
            Assert.That(npc.PlanningJob, Is.Null);
            Assert.That(npc.Plan, Is.SameAs(original));
            npc.Blackboard.Remove<EntityUid>(fast);
        });
        await server.WaitRunTicks(tickRate / 2 + 1);
        await server.WaitAssertion(() =>
        {
            Assert.That(npc.Blackboard.GetValue<EntityUid>(fast), Is.EqualTo(EntityUid.Invalid));
            Assert.That(npc.Blackboard.GetValue<EntityUid>(slow), Is.EqualTo(EntityUid.Invalid));
            Assert.That(npc.Plan, Is.SameAs(original));
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task InactivationCancelsThePendingPlan(bool sleep)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        using var cancellation = new CancellationTokenSource();
        await server.WaitAssertion(() =>
        {
            var npc = SpawnWaiting(server, map.GridCoords, 10f);
            var job = HoldReplacement(server, npc, cancellation);
            if (sleep)
                server.System<NPCSystem>().SleepNPC(npc.Owner, npc);
            else
                server.System<HTNSystem>().SetHTNEnabled((npc.Owner, npc), false);

            Assert.That(cancellation.IsCancellationRequested, Is.True);
            Assert.That(npc.PlanningJob, Is.Null);
            Assert.That(npc.Plan, Is.Null);
            job.Run();
            Assert.That(job.Result, Is.Null);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PausedTimeDoesNotAdvanceAWait()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        HTNComponent npc = default!;
        var before = 0f;
        await server.WaitPost(() => npc = SpawnWaiting(server, map.GridCoords, 1f));
        await server.WaitRunTicks(2);
        await server.WaitPost(() =>
        {
            before = npc.Blackboard.GetValue<float>(WaitKey);
            server.System<SharedMapSystem>().SetPaused(map.MapId, true);
        });
        await server.WaitRunTicks(60);
        await server.WaitAssertion(() =>
        {
            Assert.That(npc.Blackboard.GetValue<float>(WaitKey), Is.EqualTo(before));
            server.System<SharedMapSystem>().SetPaused(map.MapId, false);
        });
        await server.WaitRunTicks(1);
        await server.WaitAssertion(() =>
        {
            var interval = (float) server.ResolveDependency<IGameTiming>().TickPeriod.TotalSeconds;
            Assert.That(npc.Blackboard.GetValue<float>(WaitKey), Is.EqualTo(before - interval).Within(0.001f));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExplicitReplanSurvivesInvalidationWhenTheCurrentTaskAdvances()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        const string nextKey = "SchedulerTestNextWait";
        HTNComponent npc = default!;
        HTNPlan original = default!;
        await server.WaitPost(() =>
        {
            npc = SpawnWaiting(server, map.GridCoords, 0f);
            var next = new WaitOperator { Key = nextKey };
            next.Initialize(server.EntMan.EntitySysManager);
            npc.Blackboard.SetValue(nextKey, 20f);
            original = new HTNPlan([npc.Plan!.CurrentTask, new HTNPrimitiveTask { Operator = next }], [1, 0], [null, null]);
            npc.Plan = original;
            npc.RootTask = new HTNCompoundTask { Task = "NFSchedulerRankingRoot" };
            npc.Blackboard.SetValue("SchedulerRankingWait", 20f);
            npc.Blackboard.SetValue("UseHighBranch", true);
            npc.Blackboard.SetValue("UseFirstLeaf", true);
            server.System<HTNSystem>().Replan(npc);
        });
        await server.WaitRunTicks(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(npc.Plan, Is.SameAs(original));
            Assert.That(original.Index, Is.EqualTo(1));
            Assert.That(npc.PlanningJob, Is.Null);
        });
        Assert.That(await WaitFor(server, () => npc.Plan != original), Is.True);
        await server.WaitAssertion(() => Assert.That(npc.Plan?.BranchTraversalRecord, Is.EqualTo(new[] { 0, 0 })));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LowerRootBranchCannotReplaceAHigherPlanBecauseOfItsLeafIndex()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        HTNComponent npc = default!;
        HTNPlan original = default!;
        HTNPlanJob candidate = default!;
        await server.WaitPost(() =>
        {
            npc = SpawnWaiting(server, map.GridCoords, 10f);
            npc.Plan = null;
            npc.PlanAccumulator = 0f;
            npc.RootTask = new HTNCompoundTask { Task = "NFSchedulerRankingRoot" };
            npc.Blackboard.SetValue("SchedulerRankingWait", 20f);
            npc.Blackboard.SetValue("UseHighBranch", true);
        });
        Assert.That(await WaitFor(server, () => npc.Plan != null), Is.True);
        await server.WaitAssertion(() =>
        {
            original = npc.Plan!;
            Assert.That(original.BranchTraversalRecord, Is.EqualTo(new[] { 0, 1 }));
            npc.Blackboard.Remove<bool>("UseHighBranch");
            server.System<HTNSystem>().Replan(npc);
        });
        await server.WaitRunTicks(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(npc.PlanningJob, Is.Not.Null);
            candidate = npc.PlanningJob!;
        });
        Assert.That(await WaitFor(server, () => npc.PlanningJob == null), Is.True);
        await server.WaitAssertion(() =>
        {
            Assert.That(candidate.Result?.BranchTraversalRecord, Is.EqualTo(new[] { 1, 0 }));
            Assert.That(npc.Plan, Is.SameAs(original), "The first differing branch decides priority.");
            npc.Blackboard.SetValue("UseHighBranch", true);
            npc.Blackboard.SetValue("UseFirstLeaf", true);
            server.System<HTNSystem>().Replan(npc);
        });
        Assert.That(await WaitFor(server, () => npc.Plan != original), Is.True);
        await server.WaitAssertion(() => Assert.That(npc.Plan?.BranchTraversalRecord, Is.EqualTo(new[] { 0, 0 })));
        await pair.CleanReturnAsync();
    }

    private static async Task<bool> WaitFor(RobustIntegrationTest.ServerIntegrationInstance server, Func<bool> predicate)
    {
        var observed = false;
        for (var i = 0; i < 30 && !observed; i++)
        {
            await server.WaitRunTicks(1);
            await server.WaitPost(() => observed = predicate());
        }
        return observed;
    }

    private static HTNPlanJob HoldReplacement(RobustIntegrationTest.ServerIntegrationInstance server,
        HTNComponent npc, CancellationTokenSource cancellation)
    {
        npc.PlanningToken = cancellation;
        var job = new HTNPlanJob(0, server.ResolveDependency<IPrototypeManager>(), npc.RootTask,
            npc.Blackboard.ShallowClone(), null, cancellation.Token);
        npc.PlanningJob = job;
        return job;
    }

    private static HTNComponent SpawnWaiting(RobustIntegrationTest.ServerIntegrationInstance server,
        EntityCoordinates position, float duration)
    {
        var actor = server.EntMan.SpawnEntity(null, position);
        return InstallWait(server, actor, duration);
    }

    private static HTNComponent InstallWait(RobustIntegrationTest.ServerIntegrationInstance server,
        EntityUid actor, float duration)
    {
        var npc = server.EntMan.EnsureComponent<HTNComponent>(actor);
        npc.RootTask = new HTNCompoundTask { Task = "IdleCompound" };
        npc.ConstantlyReplan = false;
        npc.PlanAccumulator = 100f;
        npc.Blackboard.SetValue(WaitKey, duration);
        var wait = new WaitOperator { Key = WaitKey };
        wait.Initialize(server.EntMan.EntitySysManager);
        npc.Plan = new HTNPlan([new HTNPrimitiveTask { Operator = wait }], [0], [null]);
        return npc;
    }
}
