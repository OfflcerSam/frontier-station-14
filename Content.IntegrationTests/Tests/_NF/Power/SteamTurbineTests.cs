// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Collections.Generic;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._NF.Power.FuelModules;
using Content.Server._NF.Power.Steam;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Power.Components;
using Content.Server.Power.Generator;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared._NF.Power.FuelModules;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.Power.Generator;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._NF.Power;

public sealed class SteamTurbineTests : InteractionTest
{
    [Test]
    public async Task WaterReservoirFull()
    {
        await SpawnTarget("NFStationaryGeneratorSteamStandard");
        await PlaceInHands("Beaker");
        await Server.WaitAssertion(() =>
        {
            var entity = STarget!.Value;
            var beaker = HandSys.GetActiveItem((SPlayer, Hands))!.Value;
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(entity, "water", out var waterSolution, out _), Is.True);
            Assert.That(solutions.TryGetSolution(beaker, "beaker", out var sourceSolution, out var contents), Is.True);
            solutions.TryAddReagent(waterSolution!.Value, "Water", FixedPoint2.New(250), out _);
            solutions.TryAddReagent(sourceSolution!.Value, "Water", FixedPoint2.New(30), out _);
            Assert.That(SEntMan.System<SteamTurbineSystem>().TryFillWater(
                (entity, SEntMan.GetComponent<SteamTurbineComponent>(entity)), beaker, SPlayer, FixedPoint2.New(20)), Is.False);
            Assert.That(contents.Volume, Is.EqualTo(FixedPoint2.New(30)));
        });
    }

    [Test]
    public async Task RoomHeatAndExhaust()
    {
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.System<MapLoaderSystem>().TryLoadGrid(MapId,
                new ResPath("Maps/Test/Breathing/3by3-20oxy-80nit.yml"), out var grid), Is.True);
            var entity = SEntMan.SpawnEntity("NFStationaryGeneratorSteamStandard",
                new EntityCoordinates(grid!.Value.Owner, new Vector2(0.5f, 0.5f)));
            var atmosphere = SEntMan.System<AtmosphereSystem>();
            var environment = atmosphere.GetContainingMixture(entity)!;
            var steamSystem = SEntMan.System<SteamTurbineSystem>();
            var steam = SEntMan.GetComponent<SteamTurbineComponent>(entity);
            steam.BoilerTemperature = 650f;
            environment.Temperature = 293.15f;
            var heat = atmosphere.GetThermalEnergy(environment, atmosphere.GetHeatCapacity(environment, false));
            steamSystem.HeatRoom((entity, steam), 1f);
            Assert.That(atmosphere.GetThermalEnergy(environment, atmosphere.GetHeatCapacity(environment, false)) - heat,
                Is.EqualTo(1000f).Within(1f));
            Assert.That(environment.Temperature, Is.LessThan(300f));
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(entity, "water", out var waterSolution, out _), Is.True);
            solutions.TryAddReagent(waterSolution!.Value, "Water", FixedPoint2.New(250), out _);
            var waterVaporBefore = environment.GetMoles(Gas.WaterVapor);
            var carbonDioxideBefore = environment.GetMoles(Gas.CarbonDioxide);
            SEntMan.EventBus.RaiseLocalEvent(entity, new GeneratorUseFuel(1f / 6f));
            Assert.That(environment.GetMoles(Gas.CarbonDioxide) - carbonDioxideBefore, Is.EqualTo(0.15f).Within(0.001f));
            // Account for the solution's 0.01u packet precision.
            Assert.That(environment.GetMoles(Gas.WaterVapor) - waterVaporBefore, Is.EqualTo(0.0288f).Within(0.001f));
            Assert.That(steam.WaterLossRate * steam.VaporMolesPerUnit, Is.EqualTo(0.05f).Within(0.0001f));
        });
    }

    [TestCase("NFStationaryGeneratorCombustionStandard", "NFMachineFrame1x2")]
    [TestCase("NFStationaryGeneratorStirlingStandard", "NFMachineFrame1x2")]
    [TestCase("NFStationaryGeneratorIsotopeStandard", "NFMachineFrame1x2")]
    [TestCase("NFStationaryGeneratorSteamStandard", "NFMachineFrame2x2")]
    [TestCase("NFStationaryGeneratorSteamCommercial", "NFMachineFrame2x2")]
    public async Task LargeMachineFrames(string machine, string frame)
    {
        await Server.WaitAssertion(() =>
        {
            for (var tileX = 0; tileX < 8; tileX++)
            for (var tileY = 0; tileY < 8; tileY++)
                MapSystem.SetTile(MapData.Grid, new Robust.Shared.Maths.Vector2i(tileX, tileY), new Tile(TileMan[Plating].TileId));
        });
        await StartConstruction(frame);
        await InteractUsing(Steel, frame == "NFMachineFrame2x2" ? 20 : 10);
        await Interact(Wrench);
        await InteractUsing(Cable, frame == "NFMachineFrame2x2" ? 4 : 2);
        AssertPrototype(frame);
        await Server.WaitPost(() => SEntMan.DeleteEntity(STarget!.Value));
        await RunTicks(3);
        await SpawnTarget(machine);
        await Interact(Screw, Pry);
        AssertPrototype(frame);
        await Interact(Screw);
        AssertPrototype(machine);
        await Interact(Screw, Pry, Pry);
        AssertPrototype(frame);
        await Server.WaitAssertion(() =>
        {
            var footprint = SEntMan.System<Content.Shared._NF.Construction.MachineFootprintSystem>();
            Assert.That(footprint.CanFitBoard(STarget!.Value, machine), Is.True);
            Assert.That(footprint.CanFitBoard(STarget.Value, "Protolathe"), Is.False);
            var entity = SEntMan.SpawnEntity("MachineFrame", SEntMan.GetComponent<TransformComponent>(SPlayer).Coordinates);
            Assert.That(footprint.CanFitBoard(entity, machine), Is.False);
            var board = SEntMan.SpawnEntity(machine + "MachineCircuitboard", SEntMan.GetComponent<TransformComponent>(SPlayer).Coordinates);
            var machineBoard = SEntMan.GetComponent<Content.Shared.Construction.Components.MachineBoardComponent>(board);
            var flatpacker = SEntMan.SpawnEntity("MachineFlatpacker", SEntMan.GetComponent<TransformComponent>(SPlayer).Coordinates);
            var frameArea = frame == "NFMachineFrame2x2" ? 3 : 1;
            var frameMaterialCost = SEntMan.System<Content.Shared.Construction.SharedFlatpackSystem>().GetFlatpackCreationCost(
                (flatpacker, SEntMan.GetComponent<Content.Shared.Construction.Components.FlatpackCreatorComponent>(flatpacker)),
                (board, machineBoard));
            var materialCosts = SEntMan.System<Content.Shared.Construction.MachinePartSystem>().GetMachineBoardMaterialCost((board, machineBoard), -1);
            // Per extra tile: five steel sheets (500) and one LV cable (15), on top of the 750 base steel fee.
            Assert.That(frameMaterialCost["Steel"] - materialCosts.GetValueOrDefault("Steel"), Is.EqualTo(-750 - 515 * frameArea));
            Assert.That(frameMaterialCost["Silver"] - materialCosts.GetValueOrDefault("Silver"), Is.EqualTo(-100));
            Assert.That(frameMaterialCost["Gold"] - materialCosts.GetValueOrDefault("Gold"), Is.EqualTo(-50));
            var generator = SEntMan.SpawnEntity(machine, new EntityCoordinates(MapData.Grid.Owner, new Vector2(5.5f, 5.5f)));
            SEntMan.System<DamageableSystem>().TryChangeDamage(generator,
                new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(200) } }, true);
        });
        await RunTicks(3);
        await Server.WaitAssertion(() =>
        {
            var frameCount = 0;
            foreach (var nearby in SEntMan.System<EntityLookupSystem>().GetEntitiesInRange<Content.Server.Construction.Components.MachineFrameComponent>(
                         new EntityCoordinates(MapData.Grid.Owner, new Vector2(5.5f, 5.5f)), 0.3f))
            {
                Assert.That(SEntMan.GetComponent<MetaDataComponent>(nearby.Owner).EntityPrototype!.ID, Is.EqualTo(frame));
                frameCount++;
            }
            Assert.That(frameCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RuptureEffects()
    {
        EntityUid gridUid = default;
        EntityUid nearby = default;
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.System<MapLoaderSystem>().TryLoadGrid(MapId,
                new ResPath("Maps/Test/Breathing/3by3-20oxy-80nit.yml"), out var grid), Is.True);
            gridUid = grid!.Value.Owner;
            SEntMan.EnsureComponent<Content.Shared.Gravity.GravityComponent>(gridUid).Enabled = true;
            SEntMan.GetComponent<Content.Shared.Gravity.GravityComponent>(gridUid).Inherent = true;
            foreach (var nearbyEntity in SEntMan.System<EntityLookupSystem>().GetEntitiesInRange<Content.Server.Atmos.Components.AirtightComponent>(
                         new EntityCoordinates(gridUid, Vector2.Zero), 5f))
                SEntMan.DeleteEntity(nearbyEntity.Owner);
            for (var tileX = -4; tileX <= 4; tileX++)
            for (var tileY = -4; tileY <= 4; tileY++)
            {
                MapSystem.SetTile(gridUid, SEntMan.GetComponent<Robust.Shared.Map.Components.MapGridComponent>(gridUid),
                    new Robust.Shared.Maths.Vector2i(tileX, tileY), new Tile(TileMan["FloorSteel"].TileId));
                if (Math.Abs(tileX) == 4 || Math.Abs(tileY) == 4)
                    SEntMan.SpawnEntity("WallSolid", new EntityCoordinates(gridUid, new Vector2(tileX + 0.5f, tileY + 0.5f)));
            }
            nearby = SEntMan.SpawnEntity("MobHuman", new EntityCoordinates(gridUid, new Vector2(-0.5f, 0.5f)));
            Transform.SetCoordinates(SPlayer, new EntityCoordinates(gridUid, new Vector2(-0.5f, -0.5f)));
        });
        await RunTicks(100);
        await Server.WaitAssertion(() =>
        {
            foreach (var environment in SEntMan.System<AtmosphereSystem>().GetAllMixtures(gridUid, true))
            {
                if (environment.Immutable)
                    continue;
                environment.Temperature = 293.15f;
                environment.SetMoles(Gas.Oxygen, 21f);
                environment.SetMoles(Gas.Nitrogen, 83f);
            }
            var entity = SEntMan.SpawnEntity("NFStationaryGeneratorSteamStandardLiquid",
                new EntityCoordinates(gridUid, new Vector2(0.5f, 0.5f)));
            var module = SEntMan.System<FuelModuleSystem>().GetInstalledModule(entity)!.Value;
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(module, "tank", out var fuelSolution, out _), Is.True);
            solutions.TryAddReagent(fuelSolution!.Value, "WeldingFuel", FixedPoint2.New(100), out _);
            SEntMan.System<GeneratorSystem>().SetFuelGeneratorOn(entity, true);
            var steam = SEntMan.GetComponent<SteamTurbineComponent>(entity);
            Assert.That(SEntMan.System<SharedSolutionContainerSystem>().TryGetSolution(entity, "water", out var burstWater, out _), Is.True);
            SEntMan.System<SharedSolutionContainerSystem>().TryAddReagent(burstWater!.Value, "Water", FixedPoint2.New(250), out _);
            steam.BoilerTemperature = 650f;
            steam.SteamPressure = 1f;
            SEntMan.System<SteamTurbineSystem>().RuptureSteam((entity, steam));
            Assert.That(SEntMan.HasComponent<Content.Shared.Stunnable.KnockedDownComponent>(nearby), Is.True);
            Assert.That(SEntMan.System<AtmosphereSystem>().IsHotspotActive(gridUid, new Robust.Shared.Maths.Vector2i(-1, 0)), Is.True);
            Assert.That(SEntMan.System<GeneratorSystem>().GetFuel(entity), Is.LessThan(100f));
            SEntMan.DeleteEntity(entity);
        });
        await RunTicks(5);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<DamageableComponent>(nearby).TotalDamage, Is.GreaterThan(FixedPoint2.Zero));
            var explosionAudioCount = 0;
            var query = SEntMan.EntityQueryEnumerator<Robust.Shared.Audio.Components.AudioComponent>();
            while (query.MoveNext(out var explosionAudio))
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(explosionAudio.FileName, @"/Audio/Effects/explosion[1-6]\.ogg$"))
                    explosionAudioCount++;
            }
            Assert.That(explosionAudioCount, Is.EqualTo(1), "The existing explosion system must play one normal explosion clip after the turbine is deleted.");
            for (var tileX = -1; tileX <= 1; tileX++)
            for (var tileY = -1; tileY <= 1; tileY++)
            {
                var tile = MapSystem.GetTileRef(gridUid,
                    SEntMan.GetComponent<Robust.Shared.Map.Components.MapGridComponent>(gridUid),
                    new Robust.Shared.Maths.Vector2i(tileX, tileY));
                Assert.That(tile.Tile.IsEmpty, Is.False);
                Assert.That(((Content.Shared.Maps.ContentTileDefinition) TileMan[tile.Tile.TypeId]).MapAtmosphere, Is.False);
            }
        });
    }

    [Test]
    public async Task PressurizedRuptureIgnitesFuel()
    {
        EntityUid target = default;
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.System<MapLoaderSystem>().TryLoadGrid(MapId,
                new ResPath("Maps/Test/Breathing/3by3-20oxy-80nit.yml"), out var grid), Is.True);
            var coordinates = new EntityCoordinates(grid!.Value.Owner, new Vector2(0.5f, 0.5f));
            // The breathing fixture is a single open tile surrounded by walls; clear the turbine's footprint.
            foreach (var nearbyEntity in SEntMan.System<EntityLookupSystem>().GetEntitiesInRange<Content.Server.Atmos.Components.AirtightComponent>(coordinates, 3f))
                SEntMan.DeleteEntity(nearbyEntity.Owner);
            var entity = SEntMan.SpawnEntity("NFStationaryGeneratorSteamStandardLiquid", coordinates);
            target = SEntMan.SpawnEntity("NFStationaryGeneratorStirlingCompact",
                new EntityCoordinates(grid.Value.Owner, new Vector2(1.5f, 0.5f)));
            var module = SEntMan.System<FuelModuleSystem>().GetInstalledModule(entity);
            Assert.That(module, Is.Not.Null);
            var solution = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solution.TryGetSolution(module!.Value, "tank", out var fuelSolution, out _), Is.True);
            Assert.That(solution.TryGetSolution(entity, "water", out var waterSolution, out _), Is.True);
            solution.TryAddReagent(fuelSolution!.Value, "WeldingFuel", FixedPoint2.New(100), out _);
            solution.TryAddReagent(waterSolution!.Value, "Water", FixedPoint2.New(100), out _);
            SEntMan.System<GeneratorSystem>().SetFuelGeneratorOn(entity, true);
            var steam = SEntMan.GetComponent<SteamTurbineComponent>(entity);
            Assert.That(SEntMan.System<SharedSolutionContainerSystem>().TryGetSolution(entity, "water", out var burstWater, out _), Is.True);
            SEntMan.System<SharedSolutionContainerSystem>().TryAddReagent(burstWater!.Value, "Water", FixedPoint2.New(250), out _);
            steam.BoilerTemperature = 650f;
            steam.SteamPressure = 1f;
            SEntMan.System<SteamTurbineSystem>().RuptureSteam((entity, steam));
            Assert.That(SEntMan.GetComponent<FlammableComponent>(module.Value).OnFire, Is.True);
            Assert.That(steam.Ruptured, Is.True);
        });
        await RunTicks(3);
        await Server.WaitAssertion(() =>
            Assert.That(!SEntMan.HasComponent<DamageableComponent>(target) ||
                        SEntMan.GetComponent<DamageableComponent>(target).TotalDamage > FixedPoint2.Zero,
                Is.True));
    }

    [Test]
    public async Task WaterFillingAndWarmup()
    {
        await SpawnTarget("NFStationaryGeneratorSteamStandard");
        await PlaceInHands("Beaker");
        await Server.WaitAssertion(() =>
        {
            var beaker = HandSys.GetActiveItem((SPlayer, Hands))!.Value;
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(beaker, "beaker", out var beakerSolution, out _), Is.True);
            solutions.TryAddReagent(beakerSolution!.Value, "Water", FixedPoint2.New(50), out _);
        });
        await Interact();
        await Server.WaitAssertion(() =>
        {
            var entity = STarget!.Value;
            var steamSystem = SEntMan.System<SteamTurbineSystem>();
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            var steam = SEntMan.GetComponent<SteamTurbineComponent>(entity);
            var generator = SEntMan.GetComponent<FuelGeneratorComponent>(entity);
            var supplier = SEntMan.GetComponent<PowerSupplierComponent>(entity);
            Assert.That(solutions.TryGetSolution(entity, "water", out var waterSolution, out _), Is.True);
            Assert.That(steamSystem.GetWaterLevel((entity, steam)), Is.GreaterThan(0f));
            var start = new Content.Server._NF.Power.EntitySystems.GeneratorStartAttemptEvent();
            SEntMan.EventBus.RaiseLocalEvent(entity, ref start);
            Assert.That(start.FailureMessage, Is.Not.Null);
            solutions.TryAddReagent(waterSolution!.Value, "Water", FixedPoint2.New(250), out _);
            Assert.That(steamSystem.GetWaterLevel((entity, steam)), Is.EqualTo(250f));
            SEntMan.System<GeneratorSystem>().SetFuelGeneratorOn(entity, true, generator);
            for (var second = 0; second < 60; second++)
                steamSystem.UpdateSteam((entity, steam), 1f / 6f, 1f);
            Assert.That(steam.BoilerTemperature, Is.GreaterThan(625f));
            Assert.That(steam.SteamPressure, Is.GreaterThan(0.85f));
            Assert.That(supplier.MaxSupply, Is.GreaterThan(30000f));
            Assert.That(steamSystem.GetWaterLevel((entity, steam)), Is.LessThan(250f));
            var markup = SEntMan.System<ExamineSystemShared>().GetExamineText(entity, SPlayer).ToString();
            Assert.That(markup, Does.Contain("water gauge"));
            Assert.That(markup, Does.Contain("steam pressure gauge"));
            SEntMan.EventBus.RaiseLocalEvent(entity, new PortableGeneratorSetTargetPowerMessage(60f));
            for (var second = 0; second < 35; second++)
                steamSystem.UpdateSteam((entity, steam), MathF.Pow(1.5f, generator.FuelEfficiencyConstant) / 6f, 1f);
            Assert.That(steam.BoilerTemperature, Is.GreaterThan(800f));
            Assert.That(steam.SteamPressure, Is.GreaterThan(1.5f));
            Assert.That(supplier.MaxSupply, Is.GreaterThan(55000f));
            SEntMan.EventBus.RaiseLocalEvent(entity, new PortableGeneratorSetTargetPowerMessage(40f));
            steam.BoilerTemperature = 650f;
            solutions.RemoveReagent(waterSolution.Value, "Water",
                FixedPoint2.New(steamSystem.GetWaterLevel((entity, steam)) - 20f));
            steamSystem.UpdateSteam((entity, steam), 1f / 6f, 1f);
            Assert.That(supplier.MaxSupply, Is.LessThan(40000f));
            Assert.That(generator.On, Is.True);
            solutions.RemoveReagent(waterSolution.Value, "Water",
                FixedPoint2.New(steamSystem.GetWaterLevel((entity, steam)) - 5f));
            steamSystem.UpdateSteam((entity, steam), 1f / 6f, 1f);
            Assert.That(generator.On, Is.False);
        });
    }

    [Test]
    public async Task FuelHeatAndOutput()
    {
        await SpawnTarget("NFStationaryGeneratorSteamStandard");
        await Interact(Screw, "NFLiquidFuelTankStandard");
        await Server.WaitAssertion(() =>
        {
            var entity = STarget!.Value;
            var steamSystem = SEntMan.System<SteamTurbineSystem>();
            var steam = SEntMan.GetComponent<SteamTurbineComponent>(entity);
            var module = SEntMan.System<FuelModuleSystem>().GetInstalledModule(entity);
            Assert.That(module, Is.Not.Null);
            var moduleComponent = SEntMan.GetComponent<FuelModuleComponent>(module!.Value);
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(module.Value, "tank", out var fuelSolution, out _), Is.True);
            solutions.TryAddReagent(fuelSolution!.Value, "WeldingFuel", FixedPoint2.New(100), out _);
            Assert.That(steamSystem.GetFuelHeatFactor((module.Value, moduleComponent), steam), Is.EqualTo(1f));
            SEntMan.System<GeneratorSystem>().SetFuelGeneratorOn(entity, true);
            steamSystem.Update(1f);
            var weldingBurnRate = SEntMan.GetComponent<FuelGeneratorComponent>(entity).OptimalBurnRate;
            Assert.That(weldingBurnRate, Is.EqualTo(1f / 6f).Within(0.001f));
            solutions.RemoveReagent(fuelSolution.Value, "WeldingFuel", FixedPoint2.New(100));
            solutions.TryAddReagent(fuelSolution.Value, "Ethanol", FixedPoint2.New(100), out _);
            Assert.That(steamSystem.GetFuelHeatFactor((module.Value, moduleComponent), steam), Is.EqualTo(0.85f));
            steamSystem.Update(1f);
            Assert.That(SEntMan.GetComponent<FuelGeneratorComponent>(entity).OptimalBurnRate,
                Is.GreaterThan(weldingBurnRate));
            Assert.That(steam.SolidHeatFactors["Wood"], Is.EqualTo(0.75f));
            Assert.That(steam.SolidHeatFactors["FuelGradePlasma"], Is.EqualTo(steam.SolidHeatFactors["Plasma"]));
            Assert.That(steam.LiquidHeatFactors["WeldingFuel"], Is.EqualTo(1f));
            Assert.That(steam.LiquidHeatFactors["Ethanol"], Is.EqualTo(0.85f));
            Assert.That(steamSystem.GetWaterLevel((entity, steam)), Is.Zero);
            Assert.That(SEntMan.GetComponent<FuelGeneratorComponent>(entity).TargetPower, Is.EqualTo(40000f));
            var generator = SEntMan.GetComponent<FuelGeneratorComponent>(entity);
            Assert.That(generator.MaxTargetPower, Is.EqualTo(60000f));
            SEntMan.EventBus.RaiseLocalEvent(entity, new PortableGeneratorSetTargetPowerMessage(60f));
            steamSystem.Update(1f);
            Assert.That(generator.OptimalBurnRate * MathF.Pow(1.5f, generator.FuelEfficiencyConstant),
                Is.GreaterThan(weldingBurnRate * 1.9f));
        });
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.System<MapLoaderSystem>().TryLoadGrid(MapId,
                new ResPath("Maps/Test/Breathing/3by3-20oxy-80nit.yml"), out var grid), Is.True);
            var coordinates = new EntityCoordinates(grid!.Value.Owner, new Vector2(0.5f, 0.5f));
            var entity = SEntMan.SpawnEntity("NFStationaryGeneratorSteamStandard", coordinates);
            var module = SEntMan.SpawnEntity("NFLiquidFuelTankStandard", coordinates);
            var slots = SEntMan.System<ItemSlotsSystem>();
            slots.SetLock(entity, "fuel_module", false);
            Assert.That(slots.TryInsert(entity, "fuel_module", module, SPlayer), Is.True);
            slots.SetLock(entity, "fuel_module", true);
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(module, "tank", out var fuelSolution, out _), Is.True);
            Assert.That(solutions.TryGetSolution(entity, "water", out var waterSolution, out _), Is.True);
            solutions.TryAddReagent(fuelSolution!.Value, "WeldingFuel", FixedPoint2.New(100), out _);
            solutions.TryAddReagent(waterSolution!.Value, "Water", FixedPoint2.New(100), out _);
            var atmosphere = SEntMan.System<AtmosphereSystem>().GetContainingMixture(entity)!;
            var oxygenBefore = atmosphere.GetMoles(Gas.Oxygen);
            var carbonDioxideBefore = atmosphere.GetMoles(Gas.CarbonDioxide);
            var waterVaporBefore = atmosphere.GetMoles(Gas.WaterVapor);
            var fuelBefore = SEntMan.System<GeneratorSystem>().GetFuel(entity);
            SEntMan.System<GeneratorSystem>().SetFuelGeneratorOn(entity, true);
            SEntMan.System<SteamTurbineSystem>().Update(1f);
            SEntMan.System<GeneratorSystem>().Update(1f);
            Assert.That(SEntMan.GetComponent<FuelGeneratorComponent>(entity).On, Is.True);
            Assert.That(SEntMan.System<GeneratorSystem>().GetFuel(entity), Is.LessThan(fuelBefore));
            Assert.That(atmosphere.GetMoles(Gas.Oxygen), Is.LessThan(oxygenBefore));
            Assert.That(atmosphere.GetMoles(Gas.CarbonDioxide), Is.GreaterThan(carbonDioxideBefore));
            Assert.That(atmosphere.GetMoles(Gas.WaterVapor), Is.GreaterThan(waterVaporBefore));
            Assert.That(SEntMan.GetComponent<SteamTurbineComponent>(entity).BoilerTemperature, Is.GreaterThan(293.15f));
        });
    }

    [Test]
    public async Task DamageVentsSteam()
    {
        await SpawnTarget("NFStationaryGeneratorSteamStandard");
        await Server.WaitAssertion(() =>
        {
            var entity = STarget!.Value;
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            var steamSystem = SEntMan.System<SteamTurbineSystem>();
            var steam = SEntMan.GetComponent<SteamTurbineComponent>(entity);
            Assert.That(solutions.TryGetSolution(entity, "water", out var waterSolution, out _), Is.True);
            solutions.TryAddReagent(waterSolution!.Value, "Water", FixedPoint2.New(250), out _);
            SEntMan.System<GeneratorSystem>().SetFuelGeneratorOn(entity, true);
            steam.BoilerTemperature = 550f;
            steam.SteamPressure = 1f;
            var damage = SEntMan.System<DamageableSystem>();
            damage.TryChangeDamage(entity, new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(19) } }, true);
            Assert.That(steamSystem.GetWaterLevel((entity, steam)), Is.EqualTo(250f));
            damage.TryChangeDamage(entity, new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(1) } }, true);
            Assert.That(steamSystem.GetWaterLevel((entity, steam)), Is.EqualTo(245f).Within(0.01f));
            Assert.That(steam.SteamPressure, Is.LessThan(1f));
            Assert.That(steam.VaporSinceReleaseSound, Is.Zero);
            Assert.That(steam.VaporReleaseAudio, Is.Not.Null);
            Assert.That(steam.LeakAudio, Is.Not.Null);
            damage.TryChangeDamage(entity, new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(6) } }, true);
            Assert.That(steamSystem.GetWaterLevel((entity, steam)), Is.EqualTo(230f).Within(0.01f));
            Assert.That(steam.VaporSinceReleaseSound, Is.GreaterThan(14f));
            SEntMan.RemoveComponent<SteamTurbineComponent>(entity);
            Assert.That(steam.VaporReleaseAudio, Is.Null);
            Assert.That(steam.LeakAudio, Is.Null);
        });
    }

    [Test]
    public async Task PressureRupture()
    {
        await SpawnTarget("NFStationaryGeneratorSteamStandard");
        await Server.WaitAssertion(() =>
        {
            var entity = STarget!.Value;
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            var steamSystem = SEntMan.System<SteamTurbineSystem>();
            var steam = SEntMan.GetComponent<SteamTurbineComponent>(entity);
            var generator = SEntMan.GetComponent<FuelGeneratorComponent>(entity);
            Assert.That(solutions.TryGetSolution(entity, "water", out var waterSolution, out _), Is.True);
            solutions.TryAddReagent(waterSolution!.Value, "Water", FixedPoint2.New(100), out _);
            SEntMan.System<GeneratorSystem>().SetFuelGeneratorOn(entity, true, generator);
            steam.BoilerTemperature = 550f;
            steam.SteamPressure = 0f;
            steamSystem.EmitSteam((entity, steam), 5f);
            Assert.That(steam.VaporReleaseAudio, Is.Not.Null);
            steamSystem.RuptureSteam((entity, steam));
            Assert.That(steam.VaporReleaseAudio, Is.Null);
            Assert.That(steam.LeakAudio, Is.Null);
            Assert.That(steam.Ruptured, Is.True);
            Assert.That(generator.On, Is.False);
            Assert.That(steamSystem.GetWaterLevel((entity, steam)), Is.Zero);
        });
    }
}
