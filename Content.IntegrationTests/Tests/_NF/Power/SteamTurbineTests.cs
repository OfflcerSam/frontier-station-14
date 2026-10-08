// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
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
    public async Task PressurizedRuptureIgnitesFuel()
    {
        EntityUid target = default;
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.System<MapLoaderSystem>().TryLoadGrid(MapId,
                new ResPath("Maps/Test/Breathing/3by3-20oxy-80nit.yml"), out var grid), Is.True);
            var coordinates = new EntityCoordinates(grid!.Value.Owner, new Vector2(0.5f, 0.5f));
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
