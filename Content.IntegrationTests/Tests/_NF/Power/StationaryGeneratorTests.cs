// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server._NF.Chemistry;
using Content.Shared.Chemistry.Components;
using System.Collections.Generic;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._NF.Power.Components;
using Content.Server._NF.Power.EntitySystems;
using Content.Server.Power.Components;
using Content.Server.Power.Generator;
using Content.Server.Atmos.EntitySystems;
using Content.Shared.Atmos;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Utility;
using Content.Shared._NF.Construction;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Construction;
using Content.Shared.Construction.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.Power.Generator;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._NF.Power;

public sealed class StationaryGeneratorTests : InteractionTest
{
    [Test]
    public async Task RatedFuelAndPartUpgrades()
    {
        await SpawnTarget("NFStationaryGeneratorCombustionStandardLiquidEmpty");
        await Server.WaitAssertion(() =>
        {
            var entity = SEntMan.GetEntity(Target!.Value);
            var generator = SEntMan.GetComponent<FuelGeneratorComponent>(entity);
            var damageable = SEntMan.GetComponent<DamageableComponent>(entity);
            Assert.That(damageable.DamageModifierSetId, Is.EqualTo("Metallic"));
            Assert.That(SEntMan.GetComponent<RequireProjectileTargetComponent>(entity).Active, Is.False);
            SEntMan.System<DamageableSystem>().TryChangeDamage(entity,
                new DamageSpecifier { DamageDict = new() { ["Piercing"] = FixedPoint2.New(10), ["Structural"] = FixedPoint2.New(15) } });
            Assert.That(damageable.TotalDamage, Is.GreaterThan(FixedPoint2.Zero));
            Assert.That(SEntMan.System<ExamineSystemShared>().GetExamineText(entity, SPlayer).ToString(),
                Does.Contain("Its casing"));
            Assert.That(generator.MaxTargetPower, Is.EqualTo(52500f));
            var arguments = new PortableGeneratorSetTargetPowerMessage(52.5f);
            SEntMan.EventBus.RaiseLocalEvent(SEntMan.GetEntity(Target.Value), arguments);
            Assert.That(generator.TargetPower, Is.EqualTo(52500f));
            arguments = new PortableGeneratorSetTargetPowerMessage(float.NaN);
            SEntMan.EventBus.RaiseLocalEvent(SEntMan.GetEntity(Target.Value), arguments);
            Assert.That(generator.TargetPower, Is.EqualTo(52500f));
            arguments = new PortableGeneratorSetTargetPowerMessage(35f);
            SEntMan.EventBus.RaiseLocalEvent(SEntMan.GetEntity(Target.Value), arguments);
            Assert.That(generator.OptimalBurnRate * 3600f, Is.EqualTo(600f).Within(0.01f));
        });
        await Interact(Screw, "RPEDT4Filled");
        await UpgradeFuelModule();
        await Server.WaitAssertion(() =>
        {
            var entity = SEntMan.GetEntity(Target!.Value);
            var generator = SEntMan.GetComponent<FuelGeneratorComponent>(entity);
            var supplier = SEntMan.GetComponent<PowerSupplierComponent>(entity);
            Assert.That(generator.MaxTargetPower, Is.EqualTo(63000f).Within(0.01f));
            Assert.That(generator.OptimalBurnRate * 3600f, Is.EqualTo(510f).Within(0.01f));

            SEntMan.System<StationaryGeneratorSystem>().ApplyPartRatings(
                (entity, SEntMan.GetComponent<StationaryGeneratorComponent>(entity)), 4f, 4f, 4f);
            SEntMan.System<StationaryGeneratorSystem>().ApplyPartRatings(
                (entity, SEntMan.GetComponent<StationaryGeneratorComponent>(entity)), 4f, 4f, 4f);
            Assert.That(generator.MaxTargetPower, Is.EqualTo(63000f).Within(0.01f));
            Assert.That(generator.TargetPower, Is.EqualTo(35000f));
            Assert.That(generator.OptimalBurnRate * 3600f, Is.EqualTo(510f).Within(0.01f));
            Assert.That(supplier.SupplyRampRate, Is.EqualTo(12687.5f).Within(0.01f));

            var solution = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solution.TryGetSolution(FuelContainer(entity), "tank", out var fuelSolution), Is.True);
            Assert.That(fuelSolution!.Value.Comp.Solution.MaxVolume, Is.EqualTo(FixedPoint2.New(2880)));
            solution.TryAddReagent(fuelSolution.Value, "WeldingFuel", FixedPoint2.New(10), out _);
            solution.TryAddReagent(fuelSolution.Value, "Ethanol", FixedPoint2.New(10), out _);
            solution.TryAddReagent(fuelSolution.Value, "Plasma", FixedPoint2.New(10), out _);
            Assert.That(SEntMan.System<GeneratorSystem>().GetFuel(entity), Is.EqualTo(30f).Within(0.01f));
            Assert.That(SEntMan.System<GeneratorSystem>().GetIsClogged(entity), Is.False);
            // The interaction test map is in vacuum: shutdown must happen before fuel is consumed.
            SEntMan.System<GeneratorSystem>().SetFuelGeneratorOn(entity, true);
            SEntMan.System<GeneratorSystem>().Update(1f);
            Assert.That(generator.On, Is.False);
            Assert.That(SEntMan.System<GeneratorSystem>().GetFuel(entity), Is.EqualTo(30f).Within(0.01f));
            solution.TryAddReagent(fuelSolution.Value, "Water", FixedPoint2.New(1), out _);
            Assert.That(SEntMan.System<GeneratorSystem>().GetIsClogged(entity), Is.True);
        });
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.System<MapLoaderSystem>().TryLoadGrid(MapId,
                new ResPath("Maps/Test/Breathing/3by3-20oxy-80nit.yml"), out var grid), Is.True);
            var entity = SEntMan.SpawnEntity("NFStationaryGeneratorCombustionStandardEmpty",
                new EntityCoordinates(grid!.Value.Owner, new Vector2(0.5f, 0.5f)));
            var atmosphere = SEntMan.System<AtmosphereSystem>().GetContainingMixture(entity)!;
            var oxygenRequired = atmosphere.GetMoles(Gas.Oxygen);
            Assert.That(oxygenRequired, Is.GreaterThan(1f));
            var beforeFuelBurn = new GeneratorBeforeFuelBurnEvent(2f);
            SEntMan.EventBus.RaiseLocalEvent(entity, ref beforeFuelBurn);
            Assert.That(beforeFuelBurn.Cancelled, Is.False);
            Assert.That(atmosphere.GetMoles(Gas.Oxygen), Is.EqualTo(oxygenRequired - 1f).Within(0.001f));
        });
    }

    [Test]
    public async Task UpgradeThroughRPED()
    {
        await SpawnTarget("NFStationaryGeneratorCombustionStandardLiquidEmpty");
        await Interact(Screw, "RPEDT2Filled");
        await UpgradeFuelModule();
        await Server.WaitAssertion(() =>
        {
            var entity = SEntMan.GetEntity(Target!.Value);
            var generator = SEntMan.GetComponent<FuelGeneratorComponent>(entity);
            Assert.That(generator.OptimalPower, Is.EqualTo(35000f));
            Assert.That(generator.MaxTargetPower, Is.EqualTo(56000f).Within(0.01f));
            Assert.That(generator.OptimalBurnRate * 3600f, Is.EqualTo(570f).Within(0.01f));
            Assert.That(SEntMan.System<SharedSolutionContainerSystem>().TryGetSolution(FuelContainer(entity), "tank", out var fuelSolution), Is.True);
            Assert.That(fuelSolution!.Value.Comp.Solution.MaxVolume, Is.EqualTo(FixedPoint2.New(2160)));
        });
        await InteractUsing("RPEDT4Filled");
        await UpgradeFuelModule();
        await Server.WaitAssertion(() =>
        {
            var entity = SEntMan.GetEntity(Target!.Value);
            var generator = SEntMan.GetComponent<FuelGeneratorComponent>(entity);
            Assert.That(generator.OptimalPower, Is.EqualTo(35000f));
            Assert.That(generator.MaxTargetPower, Is.EqualTo(63000f).Within(0.01f));
            Assert.That(generator.OptimalBurnRate * 3600f, Is.EqualTo(510f).Within(0.01f));
            Assert.That(SEntMan.System<SharedSolutionContainerSystem>().TryGetSolution(FuelContainer(entity), "tank", out var fuelSolution), Is.True);
            Assert.That(fuelSolution!.Value.Comp.Solution.MaxVolume, Is.EqualTo(FixedPoint2.New(2880)));
        });
    }

    [Test]
    public async Task BottomlessCanCopiesSample()
    {
        await SpawnTarget("NFBottomlessJerryCan");
        await Server.WaitAssertion(() =>
        {
            var entity = SEntMan.GetEntity(Target!.Value);
            var solution = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solution.TryGetSolution(entity, "beaker", out var fuelSolution), Is.True);
            Assert.That(fuelSolution!.Value.Comp.Solution.CanReact, Is.False);
            var sample = new Solution();
            sample.AddReagent("WeldingFuel", FixedPoint2.New(10));
            sample.AddReagent("Plasma", FixedPoint2.New(10));
            Assert.That(solution.TryAddSolution(fuelSolution.Value, sample), Is.True);
            Assert.That(fuelSolution.Value.Comp.Solution.Volume, Is.EqualTo(FixedPoint2.New(200)));
            var refill = solution.SplitSolution(fuelSolution.Value, FixedPoint2.New(50));
            Assert.That(refill.GetTotalPrototypeQuantity("Plasma"), Is.EqualTo(FixedPoint2.New(25)));
            Assert.That(fuelSolution.Value.Comp.Solution.Volume, Is.EqualTo(FixedPoint2.New(200)));
            solution.SplitSolution(fuelSolution.Value, FixedPoint2.New(200));
            Assert.That(fuelSolution.Value.Comp.Solution.GetTotalPrototypeQuantity("WeldingFuel"), Is.EqualTo(FixedPoint2.New(100)));
            SEntMan.System<BottomlessSolutionSystem>().ClearSample((entity, SEntMan.GetComponent<BottomlessSolutionComponent>(entity)));
            Assert.That(fuelSolution.Value.Comp.Solution.Volume, Is.EqualTo(FixedPoint2.Zero));
            solution.TryAddReagent(fuelSolution.Value, "Water", FixedPoint2.New(1), out _);
            Assert.That(fuelSolution.Value.Comp.Solution.GetTotalPrototypeQuantity("Water"), Is.EqualTo(FixedPoint2.New(200)));
            Assert.That(fuelSolution.Value.Comp.Solution.GetTotalPrototypeQuantity("Plasma"), Is.EqualTo(FixedPoint2.Zero));
        });
    }

    [Test]
    public async Task StartFailureReportsCause()
    {
        await SpawnTarget("NFStationaryGeneratorCombustionStandardLiquidEmpty");
        await Server.WaitAssertion(() =>
        {
            var entity = SEntMan.GetEntity(Target!.Value);
            var startAttempt = new GeneratorStartAttemptEvent();
            SEntMan.EventBus.RaiseLocalEvent(entity, ref startAttempt);
            Assert.That(startAttempt.FailureMessage, Is.EqualTo("stationary-generator-empty"));
            var solution = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solution.TryGetSolution(FuelContainer(entity), "tank", out var fuelSolution), Is.True);
            solution.TryAddReagent(fuelSolution!.Value, "WeldingFuel", FixedPoint2.New(10), out _);
            startAttempt = new GeneratorStartAttemptEvent();
            SEntMan.EventBus.RaiseLocalEvent(entity, ref startAttempt);
            Assert.That(startAttempt.FailureMessage, Is.EqualTo("stationary-generator-no-oxygen"));
            solution.TryAddReagent(fuelSolution.Value, "Water", FixedPoint2.New(1), out _);
            startAttempt = new GeneratorStartAttemptEvent();
            SEntMan.EventBus.RaiseLocalEvent(entity, ref startAttempt);
            Assert.That(startAttempt.FailureMessage, Is.EqualTo("stationary-generator-contaminated"));
        });
    }

    [TestCase(0)]
    [TestCase(90)]
    [TestCase(180)]
    [TestCase(270)]
    public async Task FootprintRejectsBlockedTiles(int rotation)
    {
        await Server.WaitAssertion(() =>
        {
            // Keep the fixture floor connected so automatic grid splitting cannot move it.
            for (var tileOffset = 0; tileOffset < 64; tileOffset++)
                MapSystem.SetTile(MapData.Grid, new Vector2i(tileOffset % 8, tileOffset / 8), new Tile(TileMan[Plating].TileId));
            var coordinates = new EntityCoordinates(MapData.Grid.Owner, new Vector2(5.5f, 5.5f));
            var tileCoordinates = coordinates.Offset(Angle.FromDegrees(rotation).RotateVec(Vector2.UnitY));
            MapSystem.SetTile(MapData.Grid, coordinates, new Tile(TileMan[Plating].TileId));
            MapSystem.SetTile(MapData.Grid, tileCoordinates, new Tile(TileMan[Plating].TileId));
            var footprint = SEntMan.System<MachineFootprintSystem>();
            Assert.That(footprint.CanPlace("NFStationaryGeneratorCombustionStandardEmpty", coordinates,
                Angle.FromDegrees(rotation), SPlayer), Is.True);
            var intersectingEntity = SEntMan.SpawnEntity("WallSolid", tileCoordinates);
            Assert.That(footprint.CanPlace("NFStationaryGeneratorCombustionStandardEmpty", coordinates,
                Angle.FromDegrees(rotation), SPlayer), Is.False);
            SEntMan.DeleteEntity(intersectingEntity);
            MapSystem.SetTile(MapData.Grid, tileCoordinates, Tile.Empty);
            Assert.That(footprint.CanPlace("NFStationaryGeneratorCombustionStandardEmpty", coordinates,
                Angle.FromDegrees(rotation), SPlayer), Is.False);
        });
    }

    [Test]
    public async Task FlatpackerChargesStockParts()
    {
        await SpawnTarget("NFStationaryGeneratorCombustionStandardMachineCircuitboard");
        await Server.WaitAssertion(() =>
        {
            var entity = SEntMan.GetEntity(Target!.Value);
            var machineBoard = SEntMan.GetComponent<MachineBoardComponent>(entity);
            var materialCosts = SEntMan.System<MachinePartSystem>().GetMachineBoardMaterialCost((entity, machineBoard));
            machineBoard.Requirements.Clear();
            Assert.That(materialCosts["Steel"] - SEntMan.System<MachinePartSystem>()
                .GetMachineBoardMaterialCost((entity, machineBoard)).GetValueOrDefault("Steel"), Is.EqualTo(200));
            Assert.That(materialCosts["Plastic"] - SEntMan.System<MachinePartSystem>()
                .GetMachineBoardMaterialCost((entity, machineBoard)).GetValueOrDefault("Plastic"), Is.EqualTo(200));
        });
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








