// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server._NF.Power.Steam;
using Content.Shared.Administration.Logs;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Power.Generator;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NF.Power;

[TestFixture]
public sealed class CommercialSteamAdminLogsTests
{
    [Test]
    public async Task TargetAndRuptureLogActors()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            AdminLogsEnabled = true, DummyTicker = false, Connected = true, Dirty = true
        });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var logs = server.ResolveDependency<IAdminLogManager>();
        await pair.CreateTestMap();
        var marker = "steam-audit-" + Guid.NewGuid();
        await server.WaitAssertion(() =>
        {
            var entity = entities.SpawnEntity("NFStationaryGeneratorSteamStandardEmpty", pair.TestMap!.GridCoords);
            var actor = entities.SpawnEntity(null, pair.TestMap.GridCoords);
            entities.System<MetaDataSystem>().SetEntityName(entity, marker);
            entities.System<MetaDataSystem>().SetEntityName(actor, marker + "-operator");
            entities.EventBus.RaiseLocalEvent(entity, new PortableGeneratorSetTargetPowerMessage(60) { Actor = actor });
            entities.EventBus.RaiseLocalEvent(entity, new PortableGeneratorSetTargetPowerMessage(60) { Actor = actor });
            var solutions = entities.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(entity, "water", out var water, out _), Is.True);
            solutions.TryAddReagent(water!.Value, "Water", FixedPoint2.New(250), out _);
            var steam = entities.GetComponent<SteamTurbineComponent>(entity);
            steam.SteamPressure = 1f;
            steam.BoilerTemperature = 650f;
            entities.System<DamageableSystem>().TryChangeDamage(entity,
                new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(25) } }, true, origin: actor);
        });
        await PoolManager.WaitUntil(server, async () =>
        {
            var entries = await logs.CurrentRoundLogs(new LogFilter { Search = marker });
            if (!entries.Any(entry => entry.Message.Contains("catastrophically ruptured")))
                return false;
            Assert.That(entries.Count(entry => entry.Message.Contains("target from")), Is.EqualTo(1));
            Assert.That(entries.Single(entry => entry.Message.Contains("target from")).Message, Does.Contain(marker + "-operator"));
            var rupture = entries.Single(entry => entry.Message.Contains("catastrophically ruptured")).Message;
            Assert.That(rupture, Does.Contain(marker + "-operator"));
            Assert.That(rupture, Does.Contain("3000"));
            Assert.That(rupture, Does.Contain("650"));
            return true;
        });
        await pair.CleanReturnAsync();
    }
}
