// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
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
        await SpawnTarget("NFStationaryGeneratorCombustionStandard");
        await Server.WaitAssertion(() =>
        {
            var entity = SEntMan.GetEntity(Target!.Value);
            var generator = SEntMan.GetComponent<FuelGeneratorComponent>(entity);
            var supplier = SEntMan.GetComponent<PowerSupplierComponent>(entity);
            Assert.That(generator.MaxTargetPower, Is.EqualTo(35000f));
            Assert.That(generator.OptimalBurnRate * 3600f, Is.EqualTo(600f).Within(0.01f));

            SEntMan.System<StationaryGeneratorSystem>().ApplyPartRatings(
                (entity, SEntMan.GetComponent<StationaryGeneratorComponent>(entity)), 4f, 4f, 4f);
            SEntMan.System<StationaryGeneratorSystem>().ApplyPartRatings(
                (entity, SEntMan.GetComponent<StationaryGeneratorComponent>(entity)), 4f, 4f, 4f);
            Assert.That(generator.MaxTargetPower, Is.EqualTo(45500f).Within(0.01f));
            Assert.That(generator.TargetPower, Is.EqualTo(35000f));
            Assert.That(generator.OptimalBurnRate * 3600f, Is.EqualTo(510f).Within(0.01f));
            Assert.That(supplier.SupplyRampRate, Is.EqualTo(12687.5f).Within(0.01f));

            var solution = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solution.TryGetSolution(entity, "tank", out var fuelSolution), Is.True);
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
            var entity = SEntMan.SpawnEntity("NFStationaryGeneratorCombustionStandard",
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
            Assert.That(footprint.CanPlace("NFStationaryGeneratorCombustionStandard", coordinates,
                Angle.FromDegrees(rotation), SPlayer), Is.True);
            var intersectingEntity = SEntMan.SpawnEntity("WallSolid", tileCoordinates);
            Assert.That(footprint.CanPlace("NFStationaryGeneratorCombustionStandard", coordinates,
                Angle.FromDegrees(rotation), SPlayer), Is.False);
            SEntMan.DeleteEntity(intersectingEntity);
            MapSystem.SetTile(MapData.Grid, tileCoordinates, Tile.Empty);
            Assert.That(footprint.CanPlace("NFStationaryGeneratorCombustionStandard", coordinates,
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
}





