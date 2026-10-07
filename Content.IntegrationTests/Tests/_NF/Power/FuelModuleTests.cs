// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._NF.Power.FuelModules;
using Content.Server.Power.Generator;
using Content.Shared._NF.Power.FuelModules;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Content.Shared.Examine;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
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
    public async Task PanelControlsLoading()
    {
        await SpawnTarget("NFStationaryGeneratorStirlingCompactSolid");
        await InteractUsing(Screw);
        await InteractUsing("SheetPlasma", 2);
        await Server.WaitAssertion(() => Assert.That(SEntMan.System<GeneratorSystem>().GetFuel(STarget!.Value), Is.Zero));
        await InteractUsing(Screw);
        await InteractUsing("SheetPlasma", 2);
        await Server.WaitAssertion(() => Assert.That(SEntMan.System<GeneratorSystem>().GetFuel(STarget!.Value), Is.EqualTo(25)));
        await Delete(Target!.Value);
        await SpawnTarget("NFFuelHopperCompact");
        await PlaceInHands("SheetPlasma");
        Assert.That(await ThrowItem(), Is.True);
        await RunTicks(90);
        await Server.WaitAssertion(() => Assert.That(SEntMan.GetComponent<FuelModuleComponent>(STarget!.Value).FractionalFuel["Plasma"], Is.EqualTo(100)));
    }

    [TestCase("NFStationaryGeneratorStirlingCompactSolid", FuelModuleKind.Solid, FuelModuleSize.Compact)]
    [TestCase("NFStationaryGeneratorStirlingCompactLiquid", FuelModuleKind.Liquid, FuelModuleSize.Compact)]
    [TestCase("NFStationaryGeneratorStirlingStandardSolid", FuelModuleKind.Solid, FuelModuleSize.Standard)]
    [TestCase("NFStationaryGeneratorStirlingStandardLiquid", FuelModuleKind.Liquid, FuelModuleSize.Standard)]
    public async Task MappingVariantsIncludeModules(string prototypeId, FuelModuleKind fuel, FuelModuleSize state)
    {
        await SpawnTarget(prototypeId);
        await Server.WaitAssertion(() =>
        {
            var module = SEntMan.System<FuelModuleSystem>().GetInstalledModule(STarget!.Value);
            Assert.That(module, Is.Not.Null);
            var moduleComponent = SEntMan.GetComponent<FuelModuleComponent>(module!.Value);
            Assert.That(moduleComponent.Kind, Is.EqualTo(fuel));
            Assert.That(moduleComponent.ModuleSize, Is.EqualTo(state));
            Assert.That(SEntMan.System<GeneratorSystem>().GetFuel(STarget.Value), Is.Zero);
            var markup = SEntMan.System<ExamineSystemShared>().GetExamineText(STarget.Value, SPlayer).ToString();
            Assert.That(markup, Does.Contain("It has a fuel label for"));
            Assert.That(markup, Does.Not.Contain("installed."));
        });
        await InteractUsing(Screw);
        await Server.WaitAssertion(() =>
        {
            var markup = SEntMan.System<ExamineSystemShared>().GetExamineText(STarget!.Value, SPlayer).ToString();
            Assert.That(markup, Does.Contain("installed."));
            Assert.That(markup, Does.Not.Contain("fuel label for"));
        });
    }
    [TestCase("NFLiquidFuelTankCompact")]
    public async Task PanelControlsLoading(string prototypeId)
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
            SEntMan.System<SharedWiresSystem>().TogglePanel(STarget.Value, SEntMan.GetComponent<WiresPanelComponent>(STarget.Value), false, SPlayer);
        });
        await Interact();
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.System<GeneratorSystem>().GetFuel(STarget!.Value), Is.GreaterThan(0)));
    }

    [TestCase("NFStationaryGeneratorStirlingCompactLiquid", 500f, 100f)]
    [TestCase("NFStationaryGeneratorStirlingStandardLiquid", 1500f, 100f)]
    [TestCase("NFStationaryGeneratorCombustionStandard", 1800f, 100f)]
    [TestCase("NFLiquidFuelTankCompact", 500f, 50f)]
    public async Task FuelLeaksAboveDamageThreshold(string prototypeId, float fuelCapacity, float destructionThreshold)
    {
        await SpawnTarget(prototypeId);
        await Server.WaitAssertion(() =>
        {
            var entity = STarget!.Value;
            var module = SEntMan.System<FuelModuleSystem>().GetInstalledModule(entity) ?? entity;
            var solution = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solution.TryGetSolution(module, "tank", out var fuelSolution, out var fuel), Is.True);
            solution.TryAddReagent(fuelSolution!.Value, "WeldingFuel", FixedPoint2.New(fuelCapacity / 2), out _);
            solution.TryAddReagent(fuelSolution.Value, "Ethanol", FixedPoint2.New(fuelCapacity / 2), out _);
            var damageable = SEntMan.System<DamageableSystem>();
            damageable.TryChangeDamage(entity, new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(destructionThreshold * 0.19f) } }, true);
            Assert.That(fuel!.Volume.Float(), Is.EqualTo(fuelCapacity).Within(0.01));
            damageable.TryChangeDamage(entity, new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(destructionThreshold * 0.01f) } }, true);
            Assert.That(fuel.Volume.Float(), Is.EqualTo(fuelCapacity * 0.98f).Within(0.01));
            damageable.TryChangeDamage(entity, new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(destructionThreshold * 0.06f) } }, true);
            Assert.That(fuel.Volume.Float(), Is.EqualTo(fuelCapacity * 0.92f).Within(0.01));
            damageable.TryChangeDamage(entity, new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(-destructionThreshold * 0.07f) } }, true);
            Assert.That(fuel.Volume.Float(), Is.EqualTo(fuelCapacity * 0.92f).Within(0.01));
            damageable.TryChangeDamage(entity, new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(destructionThreshold * 0.01f) } }, true);
            Assert.That(fuel.Volume.Float(), Is.EqualTo(fuelCapacity * 0.90f).Within(0.01));
        });
    }

    [TestCase("NFStationaryGeneratorStirlingCompactSolid")]
    public async Task FuelLeaksAboveDamageThreshold(string prototypeId)
    {
        await SpawnTarget(prototypeId);
        await InteractUsing("SheetPlasma", 4);
        await Server.WaitAssertion(() =>
        {
            SEntMan.System<DamageableSystem>().TryChangeDamage(STarget!.Value,
                new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(90) } }, true);
            Assert.That(SEntMan.System<GeneratorSystem>().GetFuel(STarget.Value), Is.EqualTo(50));
        });
    }

    [TestCase("NFFuelHopperCompact", 4000f)]
    [TestCase("NFLiquidFuelTankStandard", 1500f)]
    public async Task ModuleUpgradesThroughRPED(string prototypeId, float fuelCapacity)
    {
        await SpawnTarget(prototypeId);
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







