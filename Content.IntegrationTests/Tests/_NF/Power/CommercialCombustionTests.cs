// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
#nullable enable
using System.Numerics;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._NF.Power.Components;
using Content.Server._NF.Power.EntitySystems;
using Content.Server.Power.Components;
using Content.Server.Power.Generator;
using Content.Shared._NF.Construction;
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

public sealed class CommercialCombustionTests : InteractionTest
{
    [Test]
    public async Task UpgradesAndDamageLeaks()
    {
        await SpawnTarget("NFStationaryGeneratorCombustionCommercialLiquidEmpty");
        await Server.WaitAssertion(() =>
        {
            var uid = STarget!.Value;
            var fuel = SEntMan.GetComponent<FuelGeneratorComponent>(uid);
            Assert.That(fuel.OptimalPower, Is.EqualTo(70000));
            Assert.That(fuel.MaxTargetPower, Is.EqualTo(105000));
            Assert.That(fuel.OptimalBurnRate * 3600, Is.EqualTo(1200).Within(0.01));
            Assert.That(SharedGeneratorSystem.CalcFuelEfficiency(105000, 70000, fuel),
                Is.EqualTo(1f / MathF.Pow(1.5f, 1.3f)).Within(0.0001));
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(FuelContainer(uid), "tank", out var tank, out var solution), Is.True);
            Assert.That(solution!.MaxVolume, Is.EqualTo(FixedPoint2.New(4800)));
            solutions.TryAddReagent(tank!.Value, "WeldingFuel", FixedPoint2.New(1000), out _);
            var damage = SEntMan.System<DamageableSystem>();
            damage.TryChangeDamage(uid, new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(29) } }, true);
            Assert.That(solution.Volume, Is.EqualTo(FixedPoint2.New(1000)));
            damage.TryChangeDamage(uid, new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(1) } }, true);
            Assert.That(solution.Volume, Is.EqualTo(FixedPoint2.New(904)), "First leak is 2% of the 4800u tank at 20% of 150 HP.");
        });
        await Interact(Screw, "RPEDT4Filled");
        await UpgradeFuelModule();
        await Server.WaitAssertion(() =>
        {
            var uid = STarget!.Value;
            var fuel = SEntMan.GetComponent<FuelGeneratorComponent>(uid);
            Assert.That(fuel.OptimalPower, Is.EqualTo(70000));
            Assert.That(fuel.MaxTargetPower, Is.EqualTo(126000).Within(0.01));
            Assert.That(fuel.OptimalBurnRate * 3600, Is.EqualTo(1020).Within(0.01));
            Assert.That(SEntMan.GetComponent<PowerSupplierComponent>(uid).SupplyRampRate, Is.EqualTo(25375).Within(0.01));
            Assert.That(SEntMan.System<SharedSolutionContainerSystem>().TryGetSolution(FuelContainer(uid), "tank", out _, out var solution), Is.True);
            Assert.That(solution!.MaxVolume, Is.EqualTo(FixedPoint2.New(7680)));
            Assert.That(solution.Volume, Is.EqualTo(FixedPoint2.New(904)), "Upgrading cannot create fuel.");
        });
    }

    [TestCase(0)]
    [TestCase(90)]
    [TestCase(180)]
    [TestCase(270)]
    public async Task RotatedPipingBurnAndShutdown(int degrees)
    {
        EntityUid uid = default;
        EntityUid outlet = default;
        await Server.WaitAssertion(() =>
        {
            for (var x = -5; x <= 5; x++)
            for (var y = -5; y <= 5; y++)
                MapSystem.SetTile(MapData.Grid, new Vector2i(x, y), new Tile(TileMan[Plating].TileId));
            var origin = new Vector2(0.5f, 0.5f);
            var angle = Angle.FromDegrees(degrees);
            var footprint = SEntMan.System<MachineFootprintSystem>();
            var coordinates = new EntityCoordinates(MapData.Grid.Owner, origin);
            var farTile = coordinates.Offset(angle.RotateVec(new Vector2(0, 2)));
            var wall = SEntMan.SpawnEntity("WallSolid", farTile);
            Assert.That(footprint.CanPlace("NFStationaryGeneratorCombustionCommercialEmpty", coordinates, angle, SPlayer), Is.False);
            SEntMan.DeleteEntity(wall);
            uid = SEntMan.SpawnEntity("NFStationaryGeneratorCombustionCommercialLiquidEmpty", coordinates);
            Transform.SetLocalRotation(uid, angle);
            outlet = SEntMan.SpawnEntity("GasPipeFourway", coordinates.Offset(angle.RotateVec(new Vector2(0, 3))));
            SEntMan.SpawnEntity("GasPipeFourway", coordinates.Offset(angle.RotateVec(new Vector2(0, -1))));
        });
        await RunTicks(10);
        await Server.WaitAssertion(() =>
        {
            var piping = SEntMan.System<GeneratorPipingSystem>();
            Assert.That(piping.TryGetExhaustMixture(uid, out var exhaust), Is.True);
            var intake = piping.GetIntakeMixture(uid)!;
            Assert.That(intake, Is.Not.Null);
            Assert.That(intake, Is.Not.SameAs(exhaust));
            intake.Clear(); intake.Temperature = 293.15f;
            intake.SetMoles(Gas.Oxygen, 100);
            intake.SetMoles(Gas.Nitrogen, 100);
            exhaust!.Clear(); exhaust.Temperature = 293.15f;
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(FuelContainer(uid), "tank", out var tank, out _), Is.True);
            solutions.TryAddReagent(tank!.Value, "WeldingFuel", FixedPoint2.New(100), out _);
            var generators = SEntMan.System<GeneratorSystem>();
            var before = generators.GetFuel(uid);
            generators.SetFuelGeneratorOn(uid, true);
            generators.Update(1f);
            Assert.That(generators.GetFuel(uid), Is.EqualTo(before - 1f / 3f).Within(0.011));
            Assert.That(intake.GetMoles(Gas.Oxygen), Is.EqualTo(100 - 1f / 6f).Within(0.001));
            Assert.That(exhaust.GetMoles(Gas.CarbonDioxide), Is.EqualTo(1f / 6f).Within(0.001));
            Assert.That(exhaust.GetMoles(Gas.Nitrogen), Is.EqualTo(1f / 6f).Within(0.001));
            Assert.That(SEntMan.GetComponent<OccluderComponent>(uid).BoundingBox, Is.EqualTo(new Box2(-0.5f, -0.5f, 0.5f, 2.5f)));
            exhaust.Clear(); exhaust.Temperature = 293.15f;
            exhaust.SetMoles(Gas.Nitrogen, 450 * exhaust.Volume / (Atmospherics.R * exhaust.Temperature));
            var burn = new GeneratorBeforeFuelBurnEvent(1f);
            SEntMan.EventBus.RaiseLocalEvent(uid, ref burn);
            Assert.That(burn.Cancelled, Is.False);
            Assert.That(burn.PowerMultiplier, Is.EqualTo(0.5f).Within(0.001));
            Assert.That(burn.FuelUsed, Is.EqualTo(0.5f).Within(0.001));
            exhaust.SetMoles(Gas.Nitrogen, 650 * exhaust.Volume / (Atmospherics.R * exhaust.Temperature));
            before = generators.GetFuel(uid);
            var oxygen = intake.GetMoles(Gas.Oxygen);
            generators.Update(1f);
            Assert.That(SEntMan.GetComponent<FuelGeneratorComponent>(uid).On, Is.False);
            Assert.That(generators.GetFuel(uid), Is.EqualTo(before));
            Assert.That(intake.GetMoles(Gas.Oxygen), Is.EqualTo(oxygen));
            exhaust.Clear();
            generators.Update(1f);
            Assert.That(SEntMan.GetComponent<FuelGeneratorComponent>(uid).On, Is.False, "Recovery needs manual restart.");
            SEntMan.DeleteEntity(outlet);
        });
        await RunTicks(5);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.System<GeneratorPipingSystem>().TryGetExhaustMixture(uid, out _), Is.False);
            var start = new GeneratorStartAttemptEvent();
            SEntMan.EventBus.RaiseLocalEvent(uid, ref start);
            Assert.That(start.FailureMessage, Is.EqualTo("generator-piping-disconnected"));
        });
    }

    [TestCase("NFStationaryGeneratorCombustionCommercial", false, 0)]
    [TestCase("NFStationaryGeneratorCombustionCommercialShip", true, 4800)]
    public async Task MappingSupplies(string prototype, bool anchored, int amount)
    {
        await SpawnTarget(prototype);
        await Server.WaitAssertion(() =>
        {
            var uid = STarget!.Value;
            Assert.That(SEntMan.GetComponent<TransformComponent>(uid).Anchored, Is.EqualTo(anchored));
            Assert.That(SEntMan.System<SharedSolutionContainerSystem>().TryGetSolution(FuelContainer(uid), "tank", out _, out var solution), Is.True);
            Assert.That(solution!.Volume, Is.EqualTo(FixedPoint2.New(amount)));
            Assert.That(SEntMan.GetComponent<MetaDataComponent>(uid).EntityPrototype!.HideSpawnMenu, Is.False);
        });
        await RunTicks(10);
        await Server.WaitAssertion(() => Assert.That(SEntMan.GetComponent<FuelGeneratorComponent>(STarget!.Value).On,
            Is.False, "Even a supplied Ship variant trips if its required exhaust is missing."));
    }

    private EntityUid FuelContainer(EntityUid host) =>
        SEntMan.System<Content.Server._NF.Power.FuelModules.FuelModuleSystem>().GetInstalledModule(host) ?? host;

    private async Task UpgradeFuelModule()
    {
        await Server.WaitAssertion(() =>
        {
            var module = FuelContainer(STarget!.Value);
            Assert.That(SEntMan.System<Content.Server._NF.Power.FuelModules.FuelModuleSystem>().TryExchangeModulePart(
                (module, SEntMan.GetComponent<Content.Shared._NF.Power.FuelModules.FuelModuleComponent>(module)),
                HandSys.GetActiveItem((SPlayer, Hands))!.Value), Is.True);
        });
    }
}
