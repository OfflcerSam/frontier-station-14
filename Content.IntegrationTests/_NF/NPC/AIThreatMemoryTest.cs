#nullable enable
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.UnitTesting;

namespace Content.IntegrationTests._NF.NPC;

[TestFixture, Category("AILogicRegression")]
public sealed class AIThreatMemoryTest
{
    [Test]
    public async Task DamageOutsideVisionStartsInvestigationAtTheRecordedLocation()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { ServerSeed = 12345 });
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var start = map.GridCoords.Offset(new(0.5f, 0.5f));
        var stimulus = start.Offset(new(12, 0));
        EntityUid actor = default;
        await server.WaitAssertion(() =>
        {
            FillFloor(server, map);
            actor = server.EntMan.SpawnEntity("NFMobCarp", start);
            var attacker = server.EntMan.SpawnEntity("MobHumanPathDummy", stimulus);
            ApplyDamage(server, actor, attacker);
            // The shooter continues moving unseen after impact.
            server.System<SharedTransformSystem>().SetCoordinates(attacker, stimulus.Offset(new(0, 2)));
        });

        Assert.That(await WaitFor(server, () => HasInvestigation(server, actor, stimulus)), Is.True,
            "An unseen hit should create a movement goal at the recorded stimulus.");
        await pair.CleanReturnAsync();
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task ProjectileUsesValidAttributionAndRemembersTheLastSighting(bool serviceDropsTarget, bool shooterDeleted)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { ServerSeed = 12345 });
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var launch = map.GridCoords.Offset(new(0.5f, 0.5f));
        var visible = launch.Offset(new(2, 0));
        var hidden = launch.Offset(new(0, 2));
        EntityUid actor = default;
        EntityUid attacker = default;
        await server.WaitAssertion(() =>
        {
            FillFloor(server, map);
            for (var x = 0; x < 9; x++)
                server.EntMan.SpawnEntity("WallSolid", launch.Offset(new(x, 1)));

            actor = server.EntMan.SpawnEntity("NFMobCarp", launch.Offset(new(7, 0)));
            attacker = server.EntMan.SpawnEntity("MobHumanPathDummy", launch);
            var projectile = server.EntMan.SpawnEntity("NFBulletRifle25Practice", launch);
            server.System<GunSystem>().ShootProjectile(projectile, Vector2.UnitX, Vector2.Zero, attacker, attacker);
            if (shooterDeleted)
                server.EntMan.DeleteEntity(attacker);
            else
                server.System<SharedTransformSystem>().SetCoordinates(attacker, hidden);
        });

        Assert.That(await WaitFor(server, () =>
            server.EntMan.GetComponent<DamageableComponent>(actor).TotalDamage.Float() > 0), Is.True,
            "The test projectile must deal actual damage before checking retaliation.");
        if (shooterDeleted)
        {
            await server.WaitAssertion(() =>
                Assert.That(server.System<NPCRetaliationSystem>().TryGetThreat(actor, out _), Is.False));
            await pair.CleanReturnAsync();
            return;
        }

        Assert.That(await WaitFor(server, () => HasInvestigation(server, actor, launch)), Is.True,
            "Projectile damage should lead to the launch point even though the shooter hid before impact.");
        await server.WaitAssertion(() =>
        {
            Assert.That(server.EntMan.GetComponent<DamageableComponent>(actor).TotalDamage.Float(), Is.GreaterThan(0));
            Assert.That(server.EntMan.HasComponent<NPCMeleeCombatComponent>(actor), Is.False);
            server.System<SharedTransformSystem>().SetCoordinates(attacker, visible);
        });

        Assert.That(await WaitFor(server, () =>
            server.EntMan.TryGetComponent<NPCMeleeCombatComponent>(actor, out var melee) && melee.Target == attacker &&
            server.EntMan.TryGetComponent<NPCSteeringComponent>(actor, out var steering) &&
            steering.Coordinates.EntityId == attacker), Is.True, "A visible attacker should take priority over investigation.");

        await server.WaitAssertion(() =>
        {
            server.System<SharedTransformSystem>().SetCoordinates(attacker, hidden.Offset(new(4, 0)));
            if (!serviceDropsTarget)
                return;

            // Reproduce the HTN service publishing an empty result before combat gets another tick.
            var htn = server.EntMan.GetComponent<HTNComponent>(actor);
            var combat = htn.Plan!.CurrentOperator;
            htn.Blackboard.SetValue("Target", EntityUid.Invalid);
            var status = combat.Update(htn.Blackboard, 1f / 60);
            Assert.That(status, Is.EqualTo(HTNOperatorStatus.Failed));
            var htnSystem = server.System<HTNSystem>();
            htnSystem.ShutdownTask(combat, htn.Blackboard, status);
            htnSystem.ShutdownPlan(htn);
            Assert.That(server.EntMan.GetComponent<NPCRetaliationComponent>(actor).Threat?.Location, Is.EqualTo(visible));
        });
        Assert.That(await WaitFor(server, () => HasInvestigation(server, actor, visible)), Is.True,
            "After losing visual contact, the NPC should investigate only its last verified sighting.");
        await pair.CleanReturnAsync();
    }

    [TestCase("null")]
    [TestCase("friendly")]
    [TestCase("deleted")]
    [TestCase("incapacitated")]
    [TestCase("unsupported root")]
    public async Task InvalidDamageStimuliDoNotStartInvestigation(string reason)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        await server.WaitAssertion(() =>
        {
            FillFloor(server, map);
            var actor = server.EntMan.SpawnEntity("NFMobCarp", map.GridCoords.Offset(new(0.5f, 0.5f)));
            var attacker = server.EntMan.SpawnEntity("MobHumanPathDummy", map.GridCoords.Offset(new(12.5f, 0.5f)));
            var factions = server.System<NpcFactionSystem>();
            if (reason == "friendly")
            {
                var friendly = "SimpleHostile";
                factions.ClearFactions(attacker);
                factions.AddFaction(attacker, friendly);
            }
            if (reason == "deleted")
                server.EntMan.DeleteEntity(attacker);
            if (reason == "incapacitated")
            {
                ApplyDamage(server, actor, null, 40);
                Assert.That(server.System<MobStateSystem>().IsIncapacitated(actor), Is.True);
            }
            if (reason == "unsupported root")
                server.EntMan.GetComponent<HTNComponent>(actor).RootTask = new HTNCompoundTask { Task = "MeleeFollowerCompound" };

            if (reason == "deleted")
            {
                Assert.That(server.System<NPCRetaliationSystem>().TryRetaliate(
                    (actor, server.EntMan.GetComponent<NPCRetaliationComponent>(actor)), attacker), Is.False);
            }
            else
            {
                ApplyDamage(server, actor, reason == "null" ? null : attacker);
            }
            Assert.That(server.EntMan.GetComponent<NPCRetaliationComponent>(actor).Threat, Is.Null);
            Assert.That(server.System<NPCRetaliationSystem>().TryGetThreat(actor, out _), Is.False);
            if (reason == "unsupported root")
            {
                Assert.That(server.System<NPCRetaliationSystem>().RequiresObservedTarget(actor), Is.False);
                Assert.That(factions.GetHostiles(actor), Does.Contain(attacker));
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RepeatDamageRefreshesMemoryAndCleanupPreservesNativeHostility()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        EntityUid actor = default;
        EntityUid attacker = default;
        EntityUid deleted = default;
        TimeSpan originalExpiry = default;
        TimeSpan originalThreatExpiry = default;
        TimeSpan refreshedExpiry = default;
        await server.WaitAssertion(() =>
        {
            FillFloor(server, map);
            actor = server.EntMan.SpawnEntity("NFMobCarp", map.GridCoords.Offset(new(0.5f, 0.5f)));
            attacker = server.EntMan.SpawnEntity("MobHumanPathDummy", map.GridCoords.Offset(new(12.5f, 0.5f)));
            var faction = "NanoTrasen";
            server.System<NpcFactionSystem>().AddFaction(attacker, faction);
            deleted = server.EntMan.SpawnEntity("MobHumanPathDummy", map.GridCoords.Offset(new(14.5f, 2.5f)));
            ApplyDamage(server, actor, deleted);
            server.EntMan.DeleteEntity(deleted);
            ApplyDamage(server, actor, attacker);
            var memory = server.EntMan.GetComponent<NPCRetaliationComponent>(actor);
            originalExpiry = memory.AttackMemories[attacker];
            originalThreatExpiry = memory.Threat!.Value.ExpiresAt;
        });
        await RunUntil(server, originalExpiry - TimeSpan.FromSeconds(8));
        await server.WaitAssertion(() =>
        {
            var memory = server.EntMan.GetComponent<NPCRetaliationComponent>(actor);
            var attackMemories = memory.AttackMemories;
            Assert.That(attackMemories.ContainsKey(deleted), Is.False);
            ApplyDamage(server, actor, attacker);
            refreshedExpiry = memory.AttackMemories[attacker];
            Assert.That(refreshedExpiry, Is.GreaterThan(originalExpiry));
            Assert.That(memory.Threat!.Value.ExpiresAt, Is.GreaterThan(originalThreatExpiry));
        });
        await RunUntil(server, originalExpiry + TimeSpan.FromSeconds(0.2));
        await server.WaitAssertion(() =>
        {
            Assert.That(server.System<NPCRetaliationSystem>().TryGetThreat(actor, out _), Is.False);
            var attackMemories = server.EntMan.GetComponent<NPCRetaliationComponent>(actor).AttackMemories;
            Assert.That(attackMemories.ContainsKey(attacker), Is.True,
                "Investigation expires independently of retaliation hostility.");
        });
        await RunUntil(server, refreshedExpiry + TimeSpan.FromSeconds(0.6));
        await server.WaitAssertion(() =>
        {
            var factions = server.System<NpcFactionSystem>();
            Assert.That(server.EntMan.GetComponent<NPCRetaliationComponent>(actor).AttackMemories, Is.Empty);
            Assert.That(factions.GetHostiles(actor), Does.Not.Contain(attacker));
            Assert.That(factions.GetNearbyHostiles(actor, 30).Contains(attacker), Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LastSightingCannotReviveExpiredNeutralRetaliation()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var start = map.GridCoords.Offset(new(0.5f, 0.5f));
        EntityUid actor = default;
        EntityUid attacker = default;
        TimeSpan expires = default;
        var npc = server.System<NPCSystem>();
        var retaliation = server.System<NPCRetaliationSystem>();
        var wasEnabled = npc.Enabled;
        try
        {
            await server.WaitAssertion(() =>
            {
                // Control the observation and teardown ordering independently of the HTN scheduler.
                npc.Enabled = false;
                FillFloor(server, map);
                actor = server.EntMan.SpawnEntity("NFMobCarp", start);
                attacker = server.EntMan.SpawnEntity("MobHumanPathDummy", start.Offset(new(3, 0)));
                var factions = server.System<NpcFactionSystem>();
                var neutral = "SimpleNeutral";
                factions.ClearFactions(attacker);
                factions.AddFaction(attacker, neutral);
                server.EntMan.SpawnEntity("WallSolid", start.Offset(new(2, 1)));
                ApplyDamage(server, actor, attacker);
                Assert.That(retaliation.TryGetThreat(actor, out _), Is.True);
                expires = server.EntMan.GetComponent<NPCRetaliationComponent>(actor).AttackMemories[attacker];
            });
            await RunUntil(server, expires - TimeSpan.FromSeconds(0.3));
            await server.WaitAssertion(() =>
            {
                Assert.That(server.System<NpcFactionSystem>().GetHostiles(actor), Does.Contain(attacker));
                Assert.That(retaliation.CanPursueVisibleTarget(actor, attacker), Is.True);
                server.System<SharedTransformSystem>().SetCoordinates(attacker, start.Offset(new(3, 2)));
            });
            await RunUntil(server, expires + TimeSpan.FromSeconds(0.6));
            await server.WaitAssertion(() =>
            {
                Assert.That(server.System<NpcFactionSystem>().GetHostiles(actor), Does.Not.Contain(attacker));
                retaliation.RetireObservation(actor, attacker);
                Assert.That(retaliation.TryGetThreat(actor, out _), Is.False);
            });
        }
        finally
        {
            await server.WaitPost(() => npc.Enabled = wasEnabled);
        }
        await pair.CleanReturnAsync();
    }

    private static bool HasInvestigation(RobustIntegrationTest.ServerIntegrationInstance server,
        EntityUid actor, EntityCoordinates location) =>
        server.EntMan.TryGetComponent<NPCSteeringComponent>(actor, out var steering) &&
        steering.Investigation != null && steering.Coordinates.Equals(location);

    private static async Task<bool> WaitFor(RobustIntegrationTest.ServerIntegrationInstance server, Func<bool> predicate)
    {
        var observed = false;
        for (var i = 0; i < 24 && !observed; i++)
        {
            await server.WaitRunTicks(5);
            await server.WaitPost(() => observed = predicate());
        }
        return observed;
    }

    private static async Task RunUntil(RobustIntegrationTest.ServerIntegrationInstance server, TimeSpan deadline)
    {
        var ticks = 0;
        await server.WaitPost(() =>
        {
            var timing = server.ResolveDependency<IGameTiming>();
            ticks = (int) Math.Ceiling((deadline - timing.CurTime) / timing.TickPeriod);
        });
        Assert.That(ticks, Is.GreaterThan(0));
        await server.WaitRunTicks(ticks);
    }

    private static void FillFloor(RobustIntegrationTest.ServerIntegrationInstance server, TestMapData map)
    {
        var maps = server.System<SharedMapSystem>();
        var tile = new Tile(server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
        for (var x = 0; x <= 14; x++)
        {
            for (var y = 0; y <= 2; y++)
                maps.SetTile(map.Grid.Owner, map.Grid.Comp, map.GridCoords.Offset(new(x, y)), tile);
        }
    }

    private static void ApplyDamage(RobustIntegrationTest.ServerIntegrationInstance server,
        EntityUid actor, EntityUid? origin, int amount = 1)
    {
        var blunt = server.ResolveDependency<IPrototypeManager>().Index(new ProtoId<DamageTypePrototype>("Blunt"));
        server.System<DamageableSystem>().TryChangeDamage(actor, new DamageSpecifier(blunt, amount),
            ignoreResistances: true, origin: origin);
    }
}
