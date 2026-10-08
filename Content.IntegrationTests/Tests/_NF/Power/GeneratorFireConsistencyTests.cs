// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Numerics;
using System.Linq;
using Content.Server._NF.Power.EntitySystems;
using Content.Server._NF.Power.FuelModules;
using Content.Client._NF.Power;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._NF.Power.Generator;
using Content.Server.Atmos.EntitySystems;
using Content.Shared._NF.Construction;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Content.Shared.Examine;
using Content.Shared.Fluids.Components;
using Content.Shared.IgnitionSource;
using Content.Shared.Throwing;
using Robust.Client.GameObjects;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._NF.Power;

public sealed class GeneratorFireConsistencyTests : InteractionTest
{
    [TestCase("NFStationaryGeneratorGasTurbineCommercialEmpty", 6)]
    [TestCase("NFStationaryGeneratorCombustionStandardEmpty", 2)]
    [TestCase("NFStationaryGeneratorCombustionCommercialEmpty", 3)]
    [TestCase("NFStationaryGeneratorStirlingCompactEmpty", 1)]
    [TestCase("NFStationaryGeneratorStirlingStandardEmpty", 2)]
    [TestCase("NFStationaryGeneratorSteamStandardEmpty", 4)]
    [TestCase("NFStationaryGeneratorSteamCommercialEmpty", 6)]
    public async Task IndependentFlamesMatchMachineFootprint(string prototype, int count)
    {
        await SpawnTarget(prototype);
        await Client.WaitAssertion(() =>
        {
            var host = CTarget!.Value;
            var appearance = CEntMan.System<AppearanceSystem>();
            appearance.SetData(host, FireVisuals.OnFire, true);
            appearance.SetData(host, FireVisuals.FireStacks, 2f);
            appearance.OnChangeData(host, CEntMan.GetComponent<SpriteComponent>(host));
            var visuals = CEntMan.GetComponent<MachineFireVisualsComponent>(host);
            var footprint = CEntMan.GetComponent<MachineFootprintComponent>(host);
            Assert.That(visuals.Fires.Count, Is.EqualTo(count));
            for (var i = 0; i < count; i++)
            {
                var fire = visuals.Fires[i];
                var transform = CEntMan.GetComponent<TransformComponent>(fire);
                Assert.That(transform.ParentUid, Is.EqualTo(host));
                Assert.That(transform.LocalPosition, Is.EqualTo(new Vector2(footprint.Tiles[i].X, footprint.Tiles[i].Y)));
                var sprite = CEntMan.GetComponent<SpriteComponent>(fire);
                Assert.That(sprite.Scale, Is.EqualTo(Vector2.One));
                appearance.OnChangeData(fire, sprite);
                Assert.That(sprite.AllLayers.Any(layer => layer.Visible && layer.RsiState.ToString() == "1"), Is.True);
            }
            var children = visuals.Fires.ToArray();
            appearance.SetData(host, FireVisuals.OnFire, false);
            appearance.OnChangeData(host, CEntMan.GetComponent<SpriteComponent>(host));
            Assert.That(visuals.Fires, Is.Empty);
            Assert.That(children.All(CEntMan.Deleted), Is.True);
        });
    }

    private EntityUid OxygenGrid()
    {
        Assert.That(SEntMan.System<MapLoaderSystem>().TryLoadGrid(MapId,
            new ResPath("Maps/Test/Breathing/3by3-20oxy-80nit.yml"), out var grid), Is.True);
        // The breathing map encloses a single open tile. Remove its surrounding walls for spread tests.
        foreach (var meta in SEntMan.EntityQuery<MetaDataComponent>().ToArray())
            if (meta.EntityPrototype?.ID == "WallReinforced" && SEntMan.GetComponent<TransformComponent>(meta.Owner).GridUid == grid!.Value.Owner)
                SEntMan.DeleteEntity(meta.Owner);
        return grid!.Value.Owner;
    }

    private EntityUid Puddle(EntityUid grid, int x, string reagent = "WeldingFuel")
    {
        var uid = SEntMan.SpawnEntity("Puddle", new EntityCoordinates(grid, new Vector2(x + 0.5f, 0.5f)));
        var solutions = SEntMan.System<SharedSolutionContainerSystem>();
        solutions.TryGetSolution(uid, "puddle", out var solution, out _);
        solutions.TryAddReagent(solution!.Value, reagent, FixedPoint2.New(10), out _);
        return uid;
    }

    [TestCase("WeldingFuel")]
    [TestCase("Ethanol")]
    [TestCase("Plasma")]
    public async Task FireSpreadsOnlyToFueledDryNeighbors(string reagent)
    {
        await Server.WaitAssertion(() =>
        {
            var grid = OxygenGrid();
            var source = Puddle(grid, 0, reagent);
            var target = Puddle(grid, 1, reagent);
            var water = Puddle(grid, -1, "Water");
            var fires = SEntMan.System<FuelPuddleFireSystem>();
            var flames = SEntMan.System<FlammableSystem>();
            fires.IgniteAt(source);
            flames.SetFireStacks(target, -10);
            fires.SpreadFrom(source);
            Assert.That(SEntMan.GetComponent<FlammableComponent>(target).OnFire, Is.False);
            flames.SetFireStacks(target, 0);
            fires.SpreadFrom(source);
            Assert.That(SEntMan.GetComponent<FlammableComponent>(target).OnFire, Is.True);
            Assert.That(SEntMan.GetComponent<FlammableComponent>(water).OnFire, Is.False);
        });
    }

    [Test]
    public async Task FireCannotSpreadThroughWall()
    {
        await Server.WaitAssertion(() =>
        {
            var grid = OxygenGrid();
            var source = Puddle(grid, 0);
            var target = Puddle(grid, 1);
            SEntMan.SpawnEntity("WallSolid", new EntityCoordinates(grid, new Vector2(1.5f, 0.5f)));
            var fires = SEntMan.System<FuelPuddleFireSystem>();
            fires.IgniteAt(source);
            fires.SpreadFrom(source);
            Assert.That(SEntMan.GetComponent<FlammableComponent>(target).OnFire, Is.False);
        });
    }

    [TestCase("CheapLighter", true)]
    [TestCase("CheapLighter", false)]
    [TestCase("Welder", true)]
    [TestCase("Welder", false)]
    public async Task LandingFlameIgnitesFuel(string prototype, bool lit)
    {
        await Server.WaitAssertion(() =>
        {
            var grid = OxygenGrid();
            var puddle = Puddle(grid, 0);
            var item = SEntMan.SpawnEntity(prototype, new EntityCoordinates(grid, new Vector2(0.5f, 0.5f)));
            SEntMan.System<SharedIgnitionSourceSystem>().SetIgnited(item, lit);
            SEntMan.EnsureComponent<ThrownItemComponent>(item);
            var landed = new LandEvent(SPlayer, false);
            SEntMan.EventBus.RaiseLocalEvent(item, ref landed);
            Assert.That(SEntMan.GetComponent<FlammableComponent>(puddle).OnFire, Is.EqualTo(lit));
        });
    }

    [Test]
    public async Task BurningInstalledSteamModulePublishesHostFire()
    {
        await SpawnTarget("NFStationaryGeneratorSteamStandardLiquidEmpty");
        await Server.WaitAssertion(() =>
        {
            var host = STarget!.Value;
            var module = SEntMan.System<FuelModuleSystem>().GetInstalledModule(host)!.Value;
            var flame = SEntMan.GetComponent<FlammableComponent>(module);
            var flammable = SEntMan.System<FlammableSystem>();
            flammable.SetFireStacks(module, 2, flame);
            flammable.Ignite(module, module, flame);
            var status = SEntMan.System<GeneratorStatusVisualsSystem>();
            status.UpdateFire(host, SEntMan.GetComponent<AppearanceComponent>(host));
            SEntMan.System<SharedAppearanceSystem>().TryGetData<bool>(host, FireVisuals.OnFire, out var burning);
            Assert.That(burning, Is.True);
            flammable.Extinguish(module, flame);
            status.UpdateFire(host, SEntMan.GetComponent<AppearanceComponent>(host));
            SEntMan.System<SharedAppearanceSystem>().TryGetData<bool>(host, FireVisuals.OnFire, out burning);
            Assert.That(burning, Is.False);
        });
    }

    [TestCase(350f, "The flames look hot.")]
    [TestCase(500f, "The flames look very hot.")]
    [TestCase(900f, "The flames look extremely hot.")]
    public async Task BurningPuddleExamineDescribesIntensity(float temperature, string description)
    {
        await Server.WaitAssertion(() =>
        {
            var grid = OxygenGrid();
            var puddle = Puddle(grid, 0);
            Transform.SetCoordinates(SPlayer, new EntityCoordinates(grid, new Vector2(0.5f, 0.5f)));
            SEntMan.System<AtmosphereSystem>().GetContainingMixture(puddle, false, true)!.Temperature = temperature;
            SEntMan.System<FuelPuddleFireSystem>().IgniteAt(puddle);
            var examine = SEntMan.System<ExamineSystemShared>();
            var text = examine.GetExamineText(puddle, SPlayer).ToString();
            Assert.That(text, Does.Contain("on fire"));
            Assert.That(text, Does.Contain(description));
            SEntMan.System<FlammableSystem>().Extinguish(puddle);
            Assert.That(examine.GetExamineText(puddle, SPlayer).ToString(), Does.Not.Contain("on fire"));
        });
    }
}
