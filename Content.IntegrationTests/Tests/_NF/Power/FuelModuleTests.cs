// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._NF.Power.FuelModules;
using Content.Server.Power.Generator;
using Content.Shared._NF.Power.FuelModules;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Power.Generator;
using Content.Shared.Wires;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NF.Power;

public sealed class FuelModuleTests : InteractionTest
{
    [Test]
    public async Task ModuleRetainsFuel()
    {
        await SpawnTarget("NFStationaryGeneratorStirlingCompact");
        await Interact(Screw, "NFFuelHopperCompact");
        await Server.WaitAssertion(() =>
        {
            var generator = SEntMan.System<FuelModuleSystem>();
            var module = generator.GetInstalledModule(STarget!.Value);
            Assert.That(module, Is.Not.Null);
            var moduleComponent = SEntMan.GetComponent<FuelModuleComponent>(module!.Value);
            moduleComponent.FractionalFuel["Plasma"] = 100;
            generator.ConsumeModuleFuel((module.Value, moduleComponent), 1);
            Assert.That(generator.GetModuleFuel((module.Value, moduleComponent)), Is.EqualTo(11.5f));
            Assert.That(generator.TryRemoveModule(STarget.Value, SPlayer), Is.True);
            Assert.That(generator.GetInstalledModule(STarget.Value), Is.Null);
            Assert.That(moduleComponent.FractionalFuel["Plasma"], Is.EqualTo(92));
            Assert.That(generator.TryInstallModule(STarget.Value, module.Value, SPlayer), Is.True);
            Assert.That(SEntMan.System<GeneratorSystem>().GetFuel(STarget.Value), Is.EqualTo(11.5f));
        });
    }

    [Test]
    public async Task ModuleRejectsWrongSize()
    {
        await SpawnTarget("NFStationaryGeneratorStirlingCompact");
        await Interact(Screw, "NFFuelHopperStandard");
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.System<FuelModuleSystem>().GetInstalledModule(STarget!.Value), Is.Null);
            Assert.That(HandSys.GetActiveItem((SPlayer, Hands)), Is.Not.Null);
        });
    }

    [Test]
    public async Task LidControlsLoading()
    {
        await SpawnTarget("NFFuelHopperCompact");
        await InteractUsing("SheetPlasma", 2);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<FuelModuleComponent>(STarget!.Value).FractionalFuel, Is.Empty);
            Assert.That(SEntMan.System<OpenableSystem>().TryOpen(STarget.Value), Is.True);
        });
        await InteractUsing("SheetPlasma", 2);
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.GetComponent<FuelModuleComponent>(STarget!.Value).FractionalFuel["Plasma"], Is.EqualTo(200)));
        await PlaceInHands("SheetPlasma");
        Assert.That(await ThrowItem(), Is.True);
        await RunTicks(90);
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.GetComponent<FuelModuleComponent>(STarget!.Value).FractionalFuel["Plasma"], Is.EqualTo(300)));
    }

    [Test]
    public async Task ImpactSpillsContents()
    {
        await SpawnTarget("NFFuelHopperCompact");
        await Server.WaitAssertion(() =>
        {
            var entity = STarget!.Value;
            var module = SEntMan.GetComponent<FuelModuleComponent>(entity);
            var generator = SEntMan.System<FuelModuleSystem>();
            module.FractionalFuel["Plasma"] = 850;
            Assert.That(generator.HandleImpact((entity, module), 14.9f), Is.False);
            Assert.That(generator.HandleImpact((entity, module), 15f), Is.True);
            Assert.That(SEntMan.System<OpenableSystem>().IsClosed(entity), Is.False);
            Assert.That(module.FractionalFuel["Plasma"], Is.EqualTo(650));
            Assert.That(generator.HandleImpact((entity, module), 15f), Is.False);
            Assert.That(module.FractionalFuel["Plasma"], Is.EqualTo(650));
            Assert.That(generator.ToggleLid(entity, SPlayer), Is.True);
            Assert.That(generator.ToggleLidLock(entity, SPlayer), Is.True);
            module.NextSpillTime = TimeSpan.Zero;
            Assert.That(generator.HandleImpact((entity, module), 30f), Is.False);
            Assert.That(module.FractionalFuel["Plasma"], Is.EqualTo(650));
        });
    }

    [TestCase("NFLiquidFuelTankCompact", 500f)]
    public async Task ImpactSpillsContents(string prototypeId, float fuelCapacity)
    {
        await SpawnTarget(prototypeId);
        await Server.WaitAssertion(() =>
        {
            var entity = STarget!.Value;
            var module = SEntMan.GetComponent<FuelModuleComponent>(entity);
            var solution = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solution.TryGetSolution(entity, "tank", out var fuelSolution, out var fuel), Is.True);
            solution.TryAddReagent(fuelSolution!.Value, "WeldingFuel", FixedPoint2.New(200), out _);
            solution.TryAddReagent(fuelSolution.Value, "Ethanol", FixedPoint2.New(200), out _);
            var generator = SEntMan.System<FuelModuleSystem>();
            Assert.That(generator.GetModuleFuel((entity, module)), Is.EqualTo(360).Within(0.01));
            Assert.That(generator.HandleImpact((entity, module), 15), Is.True);
            Assert.That(fuel!.Volume.Float(), Is.EqualTo(300));
            Assert.That(fuel.MaxVolume.Float(), Is.EqualTo(fuelCapacity));
            Assert.That(generator.GetModuleFuel((entity, module)), Is.EqualTo(270).Within(0.01));
            Assert.That(generator.HandleImpact((entity, module), 15), Is.False);
        });
    }

    [TestCase("NFLiquidFuelTankCompact")]
    public async Task LidControlsLoading(string prototypeId)
    {
        await SpawnTarget("NFStationaryGeneratorStirlingCompact");
        await Interact(Screw, prototypeId);
        await PlaceInHands("JerryCan");
        await Server.WaitAssertion(() =>
        {
            var entity = HandSys.GetActiveItem((SPlayer, Hands))!.Value;
            var solution = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solution.TryGetSolution(entity, "beaker", out var fuelSolution, out _), Is.True);
            solution.TryAddReagent(fuelSolution!.Value, "WeldingFuel", FixedPoint2.New(100), out _);
        });
        await Interact();
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.System<GeneratorSystem>().GetFuel(STarget!.Value), Is.Zero);
            var module = SEntMan.System<FuelModuleSystem>().GetInstalledModule(STarget.Value)!.Value;
            Assert.That(SEntMan.System<OpenableSystem>().TryOpen(module), Is.True);
        });
        await Interact();
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.System<GeneratorSystem>().GetFuel(STarget!.Value), Is.GreaterThan(0)));
    }

    [TestCase("NFFuelHopperCompact", 4000f)]
    [TestCase("NFLiquidFuelTankStandard", 1500f)]
    public async Task ModuleUpgradesThroughRPED(string prototypeId, float fuelCapacity)
    {
        await SpawnTarget(prototypeId);
        await Server.WaitAssertion(() => SEntMan.System<OpenableSystem>().TryOpen(STarget!.Value));
        await InteractUsing("RPEDT4Filled");
        await Server.WaitAssertion(() =>
        {
            var module = SEntMan.GetComponent<FuelModuleComponent>(STarget!.Value);
            Assert.That(module.MatterBinRating, Is.EqualTo(4));
            Assert.That(module.BaseCapacity, Is.EqualTo(fuelCapacity));
            if (module.Kind == FuelModuleKind.Liquid)
            {
                Assert.That(SEntMan.System<SharedSolutionContainerSystem>().TryGetSolution(STarget.Value, "tank", out _, out var solution), Is.True);
                Assert.That(solution!.MaxVolume.Float(), Is.EqualTo(fuelCapacity * 1.6f));
            }
        });
    }

    [TestCase("NFStationaryGeneratorStirlingCompact", "NFLiquidFuelTankCompact", 8000f, 9000f)]
    [TestCase("NFStationaryGeneratorStirlingStandard", "NFLiquidFuelTankStandard", 20000f, 10800f)]
    public async Task StirlingFuelEconomy(string prototypeId, string fuelPrototype, float electricalOutput, float elapsed)
    {
        await SpawnTarget(prototypeId);
        await Interact(Screw, fuelPrototype);
        await Server.WaitAssertion(() =>
        {
            var entity = STarget!.Value;
            var module = SEntMan.System<FuelModuleSystem>().GetInstalledModule(entity);
            Assert.That(module, Is.Not.Null);
            var solution = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solution.TryGetSolution(module!.Value, "tank", out var fuelSolution, out var fuel), Is.True);
            solution.TryAddReagent(fuelSolution!.Value, "WeldingFuel", fuel!.MaxVolume, out _);
            var generator = SEntMan.GetComponent<FuelGeneratorComponent>(entity);
            Assert.That(generator.OptimalPower, Is.EqualTo(electricalOutput));
            Assert.That(SEntMan.System<GeneratorSystem>().GetFuel(entity) / generator.OptimalBurnRate, Is.EqualTo(elapsed).Within(0.1f));
        });
    }
}



