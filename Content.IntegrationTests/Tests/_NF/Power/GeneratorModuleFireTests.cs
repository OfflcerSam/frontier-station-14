// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._NF.Power.FuelModules;
using Content.Server._NF.Power.Generator;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Power.Generator;
using Content.Shared._NF.Power.FuelModules;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.Fluids.Components;
using Content.Shared.Power.Generator;
using Content.Shared.Projectiles;
using Content.Shared.Wires;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._NF.Power;

public sealed class GeneratorModuleFireTests : InteractionTest
{
    [TestCase("Standard", 1800)]
    [TestCase("Commercial", 4800)]
    public async Task LiquidEjectRetainsContentsAndRequiresPanel(string size, int capacity)
    {
        await SpawnTarget($"NFStationaryGeneratorCombustion{size}LiquidEmpty");
        await Server.WaitAssertion(() =>
        {
            var host = STarget!.Value;
            var modules = SEntMan.System<FuelModuleSystem>();
            var module = modules.GetInstalledModule(host)!.Value;
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(module, "tank", out var tank, out var solution), Is.True);
            Assert.That(solution.MaxVolume, Is.EqualTo(FixedPoint2.New(capacity)));
            solutions.TryAddReagent(tank!.Value, "WeldingFuel", FixedPoint2.New(123.45), out _);
            var generators = SEntMan.System<GeneratorSystem>();
            generators.EmptyGenerator(host, SPlayer);
            Assert.That(modules.GetInstalledModule(host), Is.EqualTo(module));
            SEntMan.System<SharedWiresSystem>().TogglePanel(host, SEntMan.GetComponent<WiresPanelComponent>(host), true, SPlayer);
            generators.SetFuelGeneratorOn(host, true);
            generators.EmptyGenerator(host, SPlayer);
            Assert.That(modules.GetInstalledModule(host), Is.Null);
            Assert.That(SEntMan.GetComponent<FuelGeneratorComponent>(host).On, Is.False);
            Assert.That(solution.Volume, Is.EqualTo(FixedPoint2.New(123.45)));
            var examine = SEntMan.System<ExamineSystemShared>().GetExamineText(module, SPlayer).ToString();
            Assert.That(examine, Does.Contain("The fuel gauge reads"));
            Assert.That(examine, Does.Contain("123.45"));
            Assert.That(modules.TryInstallModule(host, module, SPlayer), Is.True);
            Assert.That(SEntMan.System<ExamineSystemShared>().GetExamineText(host, SPlayer).ToString(), Does.Contain("123.45"));
        });
    }

    [TestCase("Standard")]
    [TestCase("Commercial")]
    public async Task CombustionRejectsSolidModule(string size)
    {
        await SpawnTarget($"NFStationaryGeneratorCombustion{size}Empty");
        await Interact(Screw, $"NFFuelHopper{size}");
        await Server.WaitAssertion(() => Assert.That(SEntMan.System<FuelModuleSystem>().GetInstalledModule(STarget!.Value), Is.Null));
    }

    [Test]
    public async Task SolidEjectKeepsHopperAndFraction()
    {
        await SpawnTarget("NFStationaryGeneratorStirlingCompactSolidEmpty");
        await Server.WaitAssertion(() =>
        {
            var host = STarget!.Value;
            var modules = SEntMan.System<FuelModuleSystem>();
            var module = modules.GetInstalledModule(host)!.Value;
            var hopper = SEntMan.GetComponent<FuelModuleComponent>(module);
            hopper.FractionalFuel["Plasma"] = 123.45f;
            SEntMan.System<GeneratorSystem>().EmptyGenerator(host, SPlayer);
            Assert.That(modules.GetInstalledModule(host), Is.EqualTo(module));
            Assert.That(hopper.FractionalFuel["Plasma"], Is.EqualTo(23.45f).Within(0.001));
        });
    }

    [TestCase("Standard", 100, 3, 6)]
    [TestCase("Commercial", 150, 5, 10)]
    public async Task ShrapnelOnceThenDestruction(string size, int hp, int hitCount, int destructionCount)
    {
        await SpawnTarget($"NFStationaryGeneratorCombustion{size}LiquidEmpty");
        await Server.WaitAssertion(() =>
        {
            var host = STarget!.Value;
            var system = SEntMan.System<DamageableSystem>();
            var before = SEntMan.EntityQuery<GeneratorShrapnelComponent>().Count();
            SEntMan.System<GeneratorSystem>().SetFuelGeneratorOn(host, true);
            void Hit(float amount) => system.TryChangeDamage(host, new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(amount) } }, true);
            Hit(hp * 0.25f);
            Assert.That(SEntMan.EntityQuery<GeneratorShrapnelComponent>().Count() - before, Is.EqualTo(hitCount));
            Hit(hp * 0.25f);
            Assert.That(SEntMan.EntityQuery<GeneratorShrapnelComponent>().Count() - before, Is.EqualTo(hitCount));
            Hit(hp);
            Assert.That(SEntMan.EntityQuery<GeneratorShrapnelComponent>().Count() - before, Is.EqualTo(hitCount + destructionCount));
        });
    }

    [TestCase("Standard", 100, 6)]
    [TestCase("Commercial", 150, 10)]
    public async Task KillingHitDoesNotDoubleBurst(string size, int hp, int count)
    {
        await SpawnTarget($"NFStationaryGeneratorCombustion{size}LiquidEmpty");
        await Server.WaitAssertion(() =>
        {
            var before = SEntMan.EntityQuery<GeneratorShrapnelComponent>().Count();
            SEntMan.System<GeneratorSystem>().SetFuelGeneratorOn(STarget!.Value, true);
            SEntMan.System<DamageableSystem>().TryChangeDamage(STarget.Value,
                new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(hp) } }, true);
            Assert.That(SEntMan.EntityQuery<GeneratorShrapnelComponent>().Count() - before, Is.EqualTo(count));
        });
    }

    [TestCase("WeldingFuel")]
    [TestCase("Ethanol")]
    [TestCase("Plasma")]
    public async Task PuddlesBurnActualFuelAndOxygen(string reagent)
    {
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.System<MapLoaderSystem>().TryLoadGrid(MapId,
                new ResPath("Maps/Test/Breathing/3by3-20oxy-80nit.yml"), out var grid), Is.True);
            var uid = SEntMan.SpawnEntity("Puddle", new EntityCoordinates(grid!.Value.Owner, new Vector2(0.5f, 0.5f)));
            var puddle = SEntMan.GetComponent<PuddleComponent>(uid);
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(uid, puddle.SolutionName, out var tank, out var solution), Is.True);
            solutions.TryAddReagent(tank!.Value, reagent, FixedPoint2.New(5), out _);
            var fire = SEntMan.GetComponent<FlammableComponent>(uid);
            var flames = SEntMan.System<FlammableSystem>();
            var fuelFire = SEntMan.System<FuelPuddleFireSystem>();
            var atmos = SEntMan.System<AtmosphereSystem>();
            var air = atmos.GetContainingMixture(uid, false, true)!;
            var oxygen = air.GetMoles(Gas.Oxygen);
            var co2 = air.GetMoles(Gas.CarbonDioxide);
            fuelFire.UpdatePuddle((uid, puddle), fire, 1);
            Assert.That(fire.OnFire, Is.False, "Cold fuel needs ignition.");
            Assert.That(solution.Volume, Is.EqualTo(FixedPoint2.New(5)));
            flames.Ignite(uid, uid, fire);
            Assert.That(fire.OnFire, Is.True);
            var temperature = air.Temperature;
            fuelFire.UpdatePuddle((uid, puddle), fire, 1);
            Assert.That(solution.Volume, Is.EqualTo(FixedPoint2.New(4)));
            Assert.That(air.GetMoles(Gas.Oxygen), Is.EqualTo(oxygen - 0.5f).Within(0.0001));
            Assert.That(air.GetMoles(Gas.CarbonDioxide), Is.EqualTo(co2 + 0.5f).Within(0.0001));
            Assert.That((air.Temperature - temperature) * atmos.GetHeatCapacity(air, true), Is.EqualTo(5000).Within(1));
            var tile = MapSystem.GetTileRef(grid.Value.Owner, grid.Value.Comp, new Vector2i(0, 0));
            fuelFire.ExtinguishTile(tile);
            Assert.That(fire.OnFire, Is.False);
            fuelFire.UpdatePuddle((uid, puddle), fire, 1);
            Assert.That(solution.Volume, Is.EqualTo(FixedPoint2.New(4)), "Extinguishing stops consumption.");
            Assert.That(fire.FireStacks, Is.LessThan(0), "Native wet cooldown blocks immediate re-ignition.");
            flames.SetFireStacks(uid, 2, fire);
            flames.Ignite(uid, uid, fire);
            air.SetMoles(Gas.Oxygen, 0);
            fuelFire.UpdatePuddle((uid, puddle), fire, 1);
            Assert.That(fire.OnFire, Is.False);
            Assert.That(solution.Volume, Is.EqualTo(FixedPoint2.New(4)));
            air.SetMoles(Gas.Oxygen, oxygen);
            fuelFire.UpdatePuddle((uid, puddle), fire, 1);
            flames.Ignite(uid, uid, fire);
            fuelFire.UpdatePuddle((uid, puddle), fire, 10);
            Assert.That(solution.Volume, Is.EqualTo(FixedPoint2.Zero));
            Assert.That(fire.OnFire, Is.False);
        });
    }

    [Test]
    public async Task WaterCannotBurnFromContact()
    {
        await Server.WaitAssertion(() =>
        {
            var uid = SEntMan.SpawnEntity("Puddle", new EntityCoordinates(MapData.Grid.Owner, new Vector2(0.5f, 0.5f)));
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            solutions.TryGetSolution(uid, "puddle", out var tank, out _);
            solutions.TryAddReagent(tank!.Value, "Water", FixedPoint2.New(10), out _);
            var flame = SEntMan.GetComponent<FlammableComponent>(uid);
            SEntMan.System<FlammableSystem>().AdjustFireStacks(uid, 5, flame, ignite: true);
            Assert.That(flame.OnFire, Is.False);
        });
    }
    [Test]
    public async Task OrdinaryFuelContainerHasGauge()
    {
        await SpawnTarget("JerryCanWeldingFuel");
        await Server.WaitAssertion(() =>
        {
            var examine = SEntMan.System<ExamineSystemShared>().GetExamineText(STarget!.Value, SPlayer).ToString();
            Assert.That(examine, Does.Contain("The fuel gauge reads"));
        });
    }

    [TestCase("Standard", 100, 3)]
    [TestCase("Commercial", 150, 5)]
    public async Task SmallHitsAndStoppedEngineDoNotBurst(string size, int hp, int count)
    {
        await SpawnTarget($"NFStationaryGeneratorCombustion{size}LiquidEmpty");
        await Server.WaitAssertion(() =>
        {
            var host = STarget!.Value;
            var before = SEntMan.EntityQuery<GeneratorShrapnelComponent>().Count();
            var generator = SEntMan.System<GeneratorSystem>();
            void Hit(float amount) => SEntMan.System<DamageableSystem>().TryChangeDamage(host,
                new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(amount) } }, true);
            generator.SetFuelGeneratorOn(host, true);
            for (var i = 0; i < 3; i++) Hit(hp * 0.1f);
            generator.SetFuelGeneratorOn(host, false);
            Hit(hp * 0.25f);
            Assert.That(SEntMan.EntityQuery<GeneratorShrapnelComponent>().Count(), Is.EqualTo(before));
            generator.SetFuelGeneratorOn(host, true);
            Hit(hp * 0.25f);
            Assert.That(SEntMan.EntityQuery<GeneratorShrapnelComponent>().Count() - before, Is.EqualTo(count));
        });
    }

    [Test]
    public async Task FragmentCannotDamageBeyondRange()
    {
        await Server.WaitAssertion(() =>
        {
            var uid = SEntMan.SpawnEntity("NFGeneratorShrapnel", new EntityCoordinates(MapData.Grid.Owner, new Vector2(0.5f, 0.5f)));
            var range = SEntMan.GetComponent<GeneratorShrapnelComponent>(uid);
            var projectile = SEntMan.GetComponent<ProjectileComponent>(uid);
            range.Launched = true;
            var position = Transform.GetWorldPosition(uid);
            range.Origin = position - new Vector2(2.9f, 0);
            var hit = new ProjectileHitEvent(projectile.Damage, SPlayer);
            SEntMan.EventBus.RaiseLocalEvent(uid, ref hit);
            Assert.That(hit.Damage.GetTotal(), Is.EqualTo(FixedPoint2.New(5)));
            range.Origin = position - new Vector2(3.1f, 0);
            hit = new ProjectileHitEvent(projectile.Damage, SPlayer);
            SEntMan.EventBus.RaiseLocalEvent(uid, ref hit);
            Assert.That(hit.Damage.GetTotal(), Is.EqualTo(FixedPoint2.Zero));
        });
    }

    [Test]
    public async Task InstalledTankSpillReachesFloor()
    {
        await SpawnTarget("NFStationaryGeneratorCombustionStandardLiquidEmpty");
        await Server.WaitAssertion(() =>
        {
            var modules = SEntMan.System<FuelModuleSystem>();
            var module = modules.GetInstalledModule(STarget!.Value)!.Value;
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            solutions.TryGetSolution(module, "tank", out var tank, out var solution);
            solutions.TryAddReagent(tank!.Value, "WeldingFuel", FixedPoint2.New(40), out _);
            modules.SpillContents((module, SEntMan.GetComponent<FuelModuleComponent>(module)), 1);
            Assert.That(solution.Volume, Is.EqualTo(FixedPoint2.Zero));
            var spilled = 0f;
            foreach (var puddle in SEntMan.EntityQuery<PuddleComponent>())
                if (solutions.TryGetSolution(puddle.Owner, puddle.SolutionName, out _, out var contents))
                    spilled += contents.GetTotalPrototypeQuantity("WeldingFuel").Float();
            Assert.That(spilled, Is.EqualTo(40));
        });
    }
}
