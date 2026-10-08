// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
#nullable enable
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._NF.Power.Steam;
using Content.Server.Construction.Components;
using Content.Shared._NF.Construction;
using Content.Shared._NF.Power;
using Content.Shared._NF.Power.Isotope;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Power.Generator;
using Content.Shared.Tiles;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._NF.Power;

public sealed class GeneratorPresentationTests : InteractionTest
{
    [TestCase("Compact", "Uranium", 1)]
    [TestCase("Compact", "Bananium", 1)]
    [TestCase("Standard", "Uranium", 2)]
    [TestCase("Standard", "Bananium", 2)]
    public async Task IsotopeShipFuel(string size, string fuel, int count)
    {
        await SpawnTarget("NFStationaryGeneratorIsotope" + size + fuel + "Ship");
        await RunTicks(10);
        await Server.WaitAssertion(() =>
        {
            var uid = STarget!.Value;
            var generator = SEntMan.GetComponent<IsotopeGeneratorComponent>(uid);
            Assert.That(generator.CellSlots.Count, Is.EqualTo(count));
            foreach (var slot in generator.CellSlots)
            {
                var cellUid = SEntMan.System<ItemSlotsSystem>().GetItemOrNull(uid, slot)!.Value;
                var cell = SEntMan.GetComponent<IsotopeCellComponent>(cellUid);
                Assert.That(cell.State, Is.EqualTo(IsotopeCellState.Active));
                Assert.That(cell.FuelType!.Value.Id, Is.EqualTo("NFIsotopeFuel" + fuel));
                Assert.That(cell.RemainingLifetime, Is.GreaterThan(0));
            }
            Assert.That(SEntMan.GetComponent<MetaDataComponent>(uid).EntityPrototype!.HideSpawnMenu, Is.False);
        });
    }

    [Test]
    public async Task GeneratorIndicatorsFollowOperationPanelAndDamage()
    {
        await SpawnTarget("NFStationaryGeneratorIsotopeCompactUraniumShip");
        await RunTicks(10);
        await AssertGeneratorLayers(true, false, false);
        await Interact(Screw);
        await Server.WaitAssertion(() =>
            SEntMan.System<DamageableSystem>().TryChangeDamage(STarget!.Value,
                new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(5) } }, true));
        await RunTicks(10);
        await AssertGeneratorLayers(true, true, true);
        await Interact(Screw);
        await Server.WaitAssertion(() =>
        {
            SEntMan.System<DamageableSystem>().TryChangeDamage(STarget!.Value,
                new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(-5) } }, true);
            Transform.Unanchor(STarget!.Value);
        });
        await RunTicks(10);
        await AssertGeneratorLayers(false, false, false);
    }

    private async Task AssertGeneratorLayers(bool running, bool panel, bool damaged)
    {
        await Client.WaitAssertion(() =>
        {
            var sprite = CEntMan.GetComponent<SpriteComponent>(CTarget!.Value);
            var layers = sprite.AllLayers.ToArray();
            var sprites = CEntMan.System<SpriteSystem>();
            Assert.That(layers[sprites.LayerMapGet((CTarget.Value, sprite), GeneratorStatusLayers.Power)].RsiState.ToString(),
                Is.EqualTo(running ? "power_on" : "power_off"));
            Assert.That(layers[sprites.LayerMapGet((CTarget.Value, sprite), GeneratorStatusLayers.Panel)].Visible, Is.EqualTo(panel));
            Assert.That(layers[sprites.LayerMapGet((CTarget.Value, sprite), GeneratorStatusLayers.Damage)].Visible, Is.EqualTo(damaged));
            Assert.That(layers.Last().RsiState.ToString(), Is.EqualTo("border"));
        });
    }

    [TestCase("1x2")]
    [TestCase("1x3")]
    [TestCase("2x2")]
    [TestCase("2x3")]
    public async Task FrameSpritesRotateAndShowStages(string size)
    {
        foreach (var (prefix, state) in new[] { ("NFUnfinishedMachineFrame", "box_0"), ("NFMachineFrame", "box_1"), ("NFMachineFrameDestroyed", "destroyed") })
        {
            await SpawnTarget(prefix + size);
            await Server.WaitAssertion(() => Transform.SetLocalRotation(STarget!.Value, Angle.FromDegrees(90)));
            await RunTicks(10);
            await Client.WaitAssertion(() =>
            {
                var sprite = CEntMan.GetComponent<SpriteComponent>(CTarget!.Value);
                Assert.That(sprite.NoRotation, Is.False);
                Assert.That(sprite.SnapCardinals, Is.False);
                Assert.That(sprite.Scale, Is.EqualTo(Vector2.One));
                Assert.That(sprite.AllLayers.First().RsiState.ToString(), Is.EqualTo(state));
                Assert.That(CEntMan.GetComponent<TransformComponent>(CTarget.Value).LocalRotation.Degrees, Is.EqualTo(90).Within(0.01));
            });
            await Server.WaitPost(() => SEntMan.DeleteEntity(STarget!.Value));
            await RunTicks(3);
        }
    }

    [Test]
    public async Task FrameDotsTrackBoardAndParts()
    {
        await Server.WaitAssertion(() =>
        {
            for (var x = -3; x < 6; x++)
            for (var y = -3; y < 6; y++)
                MapSystem.SetTile(MapData.Grid, new Vector2i(x, y), new Tile(TileMan[Plating].TileId));
        });
        await SpawnTarget("NFStationaryGeneratorSteamCommercialEmpty");
        await Interact(Screw, Pry);
        AssertPrototype("NFMachineFrame2x3");
        await AssertFrameStage("ready", "box_3");
        await Interact(Pry);
        await AssertFrameStage("wired", "box_1");
        await InteractUsing("NFStationaryGeneratorSteamCommercialMachineCircuitboard");
        await AssertFrameStage("board", "box_2");
    }

    private async Task AssertFrameStage(string stage, string state)
    {
        await RunTicks(10);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.System<SharedAppearanceSystem>().TryGetData<string>(STarget!.Value,
                MachineFrameStatusVisuals.Stage, out var value), Is.True);
            Assert.That(value, Is.EqualTo(stage));
        });
        await Client.WaitAssertion(() =>
            Assert.That(CEntMan.GetComponent<SpriteComponent>(CTarget!.Value).AllLayers.First().RsiState.ToString(), Is.EqualTo(state)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CommercialRuptureRespectsExplosionProtection(bool protection)
    {
        await Server.WaitAssertion(() =>
        {
            for (var x = -12; x <= 12; x++)
            for (var y = -12; y <= 12; y++)
                MapSystem.SetTile(MapData.Grid, new Vector2i(x, y), new Tile(TileMan["FloorSteel"].TileId));
#pragma warning disable RA0002 // Test setup reproduces the map-authored Outpost protection flag.
            SEntMan.EnsureComponent<ProtectedGridComponent>(MapData.Grid.Owner).PreventExplosions = protection;
#pragma warning restore RA0002
            var machine = SEntMan.SpawnEntity("NFStationaryGeneratorSteamCommercialEmpty",
                new EntityCoordinates(MapData.Grid.Owner, new Vector2(0.5f, 0.5f)));
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(machine, "water", out var water, out _), Is.True);
            solutions.TryAddReagent(water!.Value, "Water", FixedPoint2.New(500), out _);
            var steam = SEntMan.GetComponent<SteamTurbineComponent>(machine);
            steam.BoilerTemperature = steam.MaximumBoilerTemperature;
            steam.SteamPressure = steam.MaximumSteamPressure;
            steam.MaximumBreachChance = 1f;
            SEntMan.System<DamageableSystem>().TryChangeDamage(machine,
                new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(45) } }, true);
        });
        await RunTicks(40);
        await Server.WaitAssertion(() =>
        {
            var tile = MapSystem.GetTileRef(MapData.Grid.Owner, MapData.Grid.Comp, new Vector2i(0, 0));
            if (protection)
                Assert.That(tile.Tile.TypeId, Is.EqualTo(TileMan["FloorSteel"].TileId));
            else
                Assert.That(tile.Tile.IsEmpty || ((Content.Shared.Maps.ContentTileDefinition) TileMan[tile.Tile.TypeId]).MapAtmosphere, Is.True);
        });
    }
}
