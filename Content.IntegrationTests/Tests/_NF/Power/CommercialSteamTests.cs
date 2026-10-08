// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

#nullable enable

using System.Numerics;
using System.Collections.Generic;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._NF.Power.Components;
using Content.Server._NF.Power.EntitySystems;
using Content.Server._NF.Power.FuelModules;
using Content.Server._NF.Power.Steam;
using Content.Server.Atmos.EntitySystems;
using Content.Server.NodeContainer.EntitySystems;
using Content.Server.NodeContainer.Nodes;
using Content.Server.Power.Generator;
using Content.Shared.Atmos;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Power.Generator;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._NF.Power;

public sealed class CommercialSteamTests : InteractionTest
{
    [TestCase("NFStationaryGeneratorSteamStandardEmpty", 24, 1f, false)]
    [TestCase("NFStationaryGeneratorSteamStandardEmpty", 25, 1f, true)]
    [TestCase("NFStationaryGeneratorSteamCommercialEmpty", 44, 1f, false)]
    [TestCase("NFStationaryGeneratorSteamCommercialEmpty", 45, 1f, true)]
    [TestCase("NFStationaryGeneratorSteamStandardEmpty", 25, 0f, false)]
    public async Task SingleHitRupture(string prototype, int damageAmount, float pressure, bool ruptures)
    {
        await SpawnTarget(prototype);
        var entity = STarget!.Value;
        await Server.WaitAssertion(() =>
        {
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(entity, "water", out var water, out _), Is.True);
            solutions.TryAddReagent(water!.Value, "Water", FixedPoint2.New(250), out _);
            var steam = SEntMan.GetComponent<SteamTurbineComponent>(entity);
            steam.SteamPressure = pressure;
            steam.BoilerTemperature = 650f;
            // Deliberately stopped: retained pressure, not the On flag, determines danger.
            Assert.That(SEntMan.GetComponent<FuelGeneratorComponent>(entity).On, Is.False);
            SEntMan.System<DamageableSystem>().TryChangeDamage(entity,
                new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(damageAmount) } },
                true, origin: SPlayer);
            Assert.That(steam.Ruptured, Is.EqualTo(ruptures));
        });
        await RunTicks(5);
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.EntityExists(entity), Is.EqualTo(!ruptures)));
    }

    [Test]
    public async Task GradualDamageAndRetainedPressure()
    {
        await SpawnTarget("NFStationaryGeneratorSteamStandardEmpty");
        await Server.WaitAssertion(() =>
        {
            var entity = STarget!.Value;
            var steam = SEntMan.GetComponent<SteamTurbineComponent>(entity);
            var system = SEntMan.System<SteamTurbineSystem>();
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(entity, "water", out var water, out _), Is.True);
            solutions.TryAddReagent(water!.Value, "Water", FixedPoint2.New(250), out _);
            steam.SteamPressure = 1f;
            steam.BoilerTemperature = 650f;
            SEntMan.System<DamageableSystem>().TryChangeDamage(entity,
                new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(20) } }, true);
            var ventedPressure = steam.SteamPressure;
            Assert.That(ventedPressure, Is.LessThan(1f));
            system.UpdateSteamPressure((entity, steam), 1f, 0.016f);
            Assert.That(steam.SteamPressure, Is.LessThan(ventedPressure + 0.002f), "Leaks cannot recover in one tick.");
            system.Update(1f);
            Assert.That(steam.SteamPressure, Is.GreaterThan(0f), "Turning off must retain pressure.");
            Assert.That(steam.SteamPressure, Is.LessThan(ventedPressure));
            for (var hit = 0; hit < 7; hit++)
                SEntMan.System<DamageableSystem>().TryChangeDamage(entity,
                    new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(10) } }, true);
            Assert.That(steam.Ruptured, Is.False, "Separate small hits do not accumulate into a sudden-hit trigger.");
            system.UpdateSteamPressure((entity, steam), 0f, 120f);
            Assert.That(steam.SteamPressure, Is.LessThan(steam.PressureTripThreshold));
            SEntMan.System<DamageableSystem>().TryChangeDamage(entity,
                new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(10) } }, true);
            Assert.That(steam.Ruptured, Is.True, "Final breakup still releases remaining contents.");
            Assert.That(steam.SteamPressure, Is.Zero);
        });
        await RunTicks(5);
        await Server.WaitAssertion(() =>
        {
            var audio = SEntMan.EntityQueryEnumerator<Robust.Shared.Audio.Components.AudioComponent>();
            while (audio.MoveNext(out var clip))
                Assert.That(clip.FileName, Does.Not.StartWith("/Audio/Effects/explosion"));
        });
    }

    [Test]
    public async Task CommercialPipingAndCapacity()
    {
        EntityUid entity = default;
        EntityUid exhaustPipe = default;
        EntityUid intakePipe = default;
        await Server.WaitAssertion(() =>
        {
            for (var x = -2; x <= 6; x++)
            for (var y = -2; y <= 6; y++)
                MapSystem.SetTile(MapData.Grid, new Vector2i(x, y), new Tile(TileMan[Plating].TileId));
            entity = SEntMan.SpawnEntity("NFStationaryGeneratorSteamCommercialLiquidEmpty",
                new EntityCoordinates(MapData.Grid.Owner, new Vector2(2.5f, 2.5f)));
            exhaustPipe = SEntMan.SpawnEntity("GasPipeFourway",
                new EntityCoordinates(MapData.Grid.Owner, new Vector2(2.5f, 5.5f)));
            intakePipe = SEntMan.SpawnEntity("GasPipeFourway",
                new EntityCoordinates(MapData.Grid.Owner, new Vector2(2.5f, 1.5f)));
        });
        await RunTicks(10);
        await Server.WaitAssertion(() =>
        {
            var piping = SEntMan.System<GeneratorPipingSystem>();
            Assert.That(piping.TryGetExhaustMixture(entity, out var exhaust), Is.True);
            var nodes = SEntMan.System<NodeContainerSystem>();
            Assert.That(nodes.TryGetNode(entity, "intake", out PipeNode? intake), Is.True);
            Assert.That(piping.GetIntakeMixture(entity), Is.SameAs(intake!.Air));
            intake.Air.SetMoles(Gas.Oxygen, 100f);
            intake.Air.Temperature = 293.15f;
            exhaust!.Clear();
            exhaust.Temperature = 293.15f;
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            var module = SEntMan.System<FuelModuleSystem>().GetInstalledModule(entity)!.Value;
            Assert.That(solutions.TryGetSolution(module, "tank", out var tank, out var fuel), Is.True);
            Assert.That(fuel!.MaxVolume, Is.EqualTo(FixedPoint2.New(2250)));
            solutions.TryAddReagent(tank!.Value, "WeldingFuel", FixedPoint2.New(100), out _);
            Assert.That(solutions.TryGetSolution(entity, "water", out var water, out var reservoir), Is.True);
            Assert.That(reservoir!.MaxVolume, Is.EqualTo(FixedPoint2.New(500)));
            solutions.TryAddReagent(water!.Value, "Water", FixedPoint2.New(500), out _);
            var steam = SEntMan.GetComponent<SteamTurbineComponent>(entity);
            var steamSystem = SEntMan.System<SteamTurbineSystem>();
            Assert.That(steam.CanBreachHull, Is.True);
            Assert.That(SEntMan.GetComponent<OccluderComponent>(entity).BoundingBox,
                Is.EqualTo(new Box2(-0.5f, -0.5f, 1.5f, 2.5f)));
            Assert.That(SEntMan.HasComponent<OccluderComponent>(SEntMan.SpawnEntity(
                "NFStationaryGeneratorSteamStandardEmpty", new EntityCoordinates(MapData.Grid.Owner, new Vector2(5.5f, 5.5f)))), Is.False);
            var burn = new GeneratorBeforeFuelBurnEvent(1f);
            SEntMan.EventBus.RaiseLocalEvent(entity, ref burn);
            Assert.That(burn.Cancelled, Is.False);
            Assert.That(intake.Air.GetMoles(Gas.Oxygen), Is.EqualTo(99.1f).Within(0.001f));
            SEntMan.EventBus.RaiseLocalEvent(entity, new GeneratorUseFuel(burn.FuelUsed));
            Assert.That(exhaust.GetMoles(Gas.CarbonDioxide), Is.EqualTo(0.9f).Within(0.001f));
            Assert.That(exhaust.GetMoles(Gas.WaterVapor), Is.GreaterThan(0f));
            var vaporBefore = exhaust.GetMoles(Gas.WaterVapor);
            steamSystem.EmitSteam((entity, steam), 5f);
            Assert.That(exhaust.GetMoles(Gas.WaterVapor), Is.EqualTo(vaporBefore), "Damage leaks bypass piping.");
            exhaust.Clear();
            exhaust.Temperature = 293.15f;
            exhaust.SetMoles(Gas.Nitrogen, 450f * exhaust.Volume / (Atmospherics.R * exhaust.Temperature));
            burn = new GeneratorBeforeFuelBurnEvent(1f);
            SEntMan.EventBus.RaiseLocalEvent(entity, ref burn);
            Assert.That(burn.Cancelled, Is.False);
            Assert.That(burn.FuelUsed, Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(burn.PowerMultiplier, Is.EqualTo(0.5f).Within(0.001f));
            exhaust.SetMoles(Gas.Nitrogen, 650f * exhaust.Volume / (Atmospherics.R * exhaust.Temperature));
            var oxygenBefore = intake.Air.GetMoles(Gas.Oxygen);
            var fuelBefore = SEntMan.System<GeneratorSystem>().GetFuel(entity);
            SEntMan.System<GeneratorSystem>().SetFuelGeneratorOn(entity, true);
            SEntMan.System<GeneratorSystem>().Update(0.016f);
            Assert.That(SEntMan.GetComponent<FuelGeneratorComponent>(entity).On, Is.False);
            Assert.That(SEntMan.System<GeneratorSystem>().GetFuel(entity), Is.EqualTo(fuelBefore));
            Assert.That(intake.Air.GetMoles(Gas.Oxygen), Is.EqualTo(oxygenBefore));
            SEntMan.DeleteEntity(exhaustPipe);
            SEntMan.DeleteEntity(intakePipe);
        });
        await RunTicks(5);
        await Server.WaitAssertion(() =>
        {
            var piping = SEntMan.System<GeneratorPipingSystem>();
            Assert.That(piping.TryGetExhaustMixture(entity, out _), Is.False);
            Assert.That(piping.GetIntakeMixture(entity),
                Is.SameAs(SEntMan.System<AtmosphereSystem>().GetContainingMixture(entity, false, true)));
            var start = new GeneratorStartAttemptEvent();
            SEntMan.EventBus.RaiseLocalEvent(entity, ref start);
            Assert.That(start.FailureMessage, Is.Not.Null);
        });
    }

    [TestCase("NFStationaryGeneratorSteamStandardEmpty", false)]
    [TestCase("NFStationaryGeneratorSteamCommercialEmpty", true)]
    public async Task CentralHullBreach(string prototype, bool breaches)
    {
        EntityUid entity = default;
        await Server.WaitAssertion(() =>
        {
            for (var x = -12; x <= 12; x++)
            for (var y = -12; y <= 12; y++)
                MapSystem.SetTile(MapData.Grid, new Vector2i(x, y), new Tile(TileMan["FloorSteel"].TileId));
            entity = SEntMan.SpawnEntity(prototype, new EntityCoordinates(MapData.Grid.Owner, new Vector2(0.5f, 0.5f)));
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(entity, "water", out var water, out _), Is.True);
            solutions.TryAddReagent(water!.Value, "Water", FixedPoint2.New(250), out _);
            var steam = SEntMan.GetComponent<SteamTurbineComponent>(entity);
            steam.BoilerTemperature = steam.RatedSteamTemperature;
            steam.SteamPressure = 1f;
            SEntMan.System<SteamTurbineSystem>().RuptureSteam((entity, steam));
        });
        await RunTicks(30);
        await Server.WaitAssertion(() =>
        {
            var tile = MapSystem.GetTileRef(MapData.Grid.Owner, MapData.Grid.Comp, new Vector2i(0, 0));
            Assert.That(tile.Tile.IsEmpty || ((Content.Shared.Maps.ContentTileDefinition) TileMan[tile.Tile.TypeId]).MapAtmosphere,
                Is.EqualTo(breaches));
            var outside = MapSystem.GetTileRef(MapData.Grid.Owner, MapData.Grid.Comp, new Vector2i(11, 0));
            Assert.That(outside.Tile.TypeId, Is.EqualTo(TileMan["FloorSteel"].TileId));
        });
    }

    [TestCase(0)]
    [TestCase(90)]
    [TestCase(180)]
    [TestCase(270)]
    public async Task RotatedCommercialPorts(int degrees)
    {
        EntityUid entity = default;
        await Server.WaitAssertion(() =>
        {
            for (var x = -5; x <= 5; x++)
            for (var y = -5; y <= 5; y++)
                MapSystem.SetTile(MapData.Grid, new Vector2i(x, y), new Tile(TileMan[Plating].TileId));
            var origin = new Vector2(0.5f, 0.5f);
            var rotation = Angle.FromDegrees(degrees);
            entity = SEntMan.SpawnEntity("NFStationaryGeneratorSteamCommercialEmpty",
                new EntityCoordinates(MapData.Grid.Owner, origin));
            Transform.SetLocalRotation(entity, rotation);
            SEntMan.SpawnEntity("GasPipeFourway", new EntityCoordinates(MapData.Grid.Owner,
                origin + rotation.RotateVec(new Vector2(0, 3))));
            SEntMan.SpawnEntity("GasPipeFourway", new EntityCoordinates(MapData.Grid.Owner,
                origin + rotation.RotateVec(new Vector2(0, -1))));
        });
        await RunTicks(10);
        await Server.WaitAssertion(() =>
        {
            var piping = SEntMan.System<GeneratorPipingSystem>();
            Assert.That(piping.TryGetExhaustMixture(entity, out var exhaust), Is.True);
            var nodes = SEntMan.System<NodeContainerSystem>();
            Assert.That(nodes.TryGetNode(entity, "intake", out PipeNode? intake), Is.True);
            Assert.That(piping.GetIntakeMixture(entity), Is.SameAs(intake!.Air));
            Assert.That(exhaust, Is.Not.SameAs(intake.Air), "The two ports must remain separate networks.");
            Assert.That(nodes.TryGetNode(entity, "output_hv", out Content.Server.Power.Nodes.CableDeviceNode? output), Is.True,
                "Adding pipe nodes must preserve the inherited power output.");
            Assert.That(nodes.TryGetNode(entity, "output_mv", out Content.Server.Power.Nodes.CableDeviceNode? secondaryOutput), Is.True);
            Assert.That(output!.Enabled, Is.True);
            Assert.That(secondaryOutput!.Enabled, Is.False);
            Assert.That(SEntMan.GetComponent<OccluderComponent>(entity).Enabled, Is.True);
        });
    }

}
