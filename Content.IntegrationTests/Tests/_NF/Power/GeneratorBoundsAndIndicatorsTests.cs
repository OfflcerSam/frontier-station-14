// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
#nullable enable
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._NF.Power.FuelModules;
using Content.Server._NF.Power.Steam;
using Content.Server.Explosion.EntitySystems;
using Content.Shared._NF.Power;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._NF.Power;

public sealed class GeneratorBoundsAndIndicatorsTests : InteractionTest
{
    [TestCase("Standard", 1f, 1.5f, 2f)]
    [TestCase("Standard", 1.8f, 2f, 3f)]
    [TestCase("Commercial", 1f, 2.25f, 3f)]
    [TestCase("Commercial", 1.8f, 3f, 4.5f)]
    public async Task PressureControlsBoundedRadii(string size, float pressure, float damage, float flash)
    {
        await SpawnTarget("NFStationaryGeneratorSteam" + size + "Empty");
        await Server.WaitAssertion(() =>
        {
            var uid = STarget!.Value;
            var steam = SEntMan.GetComponent<SteamTurbineComponent>(uid);
            steam.SteamPressure = pressure;
            var system = SEntMan.System<SteamTurbineSystem>();
            Assert.That(system.GetBurstRadius((uid, steam)), Is.EqualTo(damage).Within(0.001));
            Assert.That(system.GetFlashRadius((uid, steam)), Is.EqualTo(flash).Within(0.001));
            Assert.That(system.GetBreachChance((uid, steam)), Is.EqualTo(pressure == 1f ? 0.25f : 0.5f).Within(0.001));
            var explosion = SEntMan.System<ExplosionSystem>();
            Assert.That(explosion.IntensityToRadius(system.GetBurstIntensity((uid, steam)), steam.BurstIntensitySlope,
                steam.BurstMaximumIntensity), Is.EqualTo(damage).Within(0.001));
            var dry = system.GetBurstAreaRestriction((uid, steam), false);
            Assert.That(dry.FlashRadius, Is.EqualTo(damage).Within(0.001));
        });
    }

    [TestCase(0, true)]
    [TestCase(90, true)]
    [TestCase(180, true)]
    [TestCase(270, true)]
    [TestCase(90, false)]
    public async Task RuptureCannotSpaceOutsideRotatedFootprint(int degrees, bool permitBreach)
    {
        var footprint = new HashSet<Vector2i>();
        Vector2 center = default;
        EntityUid outsideWall = default;
        await Server.WaitAssertion(() =>
        {
            for (var x = -8; x <= 8; x++)
            for (var y = -8; y <= 8; y++)
                MapSystem.SetTile(MapData.Grid, new Vector2i(x, y), new Tile(TileMan["FloorSteel"].TileId));
            var origin = new Vector2(0.5f, 0.5f);
            var angle = Angle.FromDegrees(degrees);
            var uid = SEntMan.SpawnEntity("NFStationaryGeneratorSteamCommercialEmpty", new EntityCoordinates(MapData.Grid.Owner, origin));
            Transform.SetLocalRotation(uid, angle);
            for (var x = 0; x < 2; x++)
            for (var y = 0; y < 3; y++)
            {
                var rotated = angle.RotateVec(new Vector2(x, y));
                footprint.Add(new Vector2i((int)MathF.Round(rotated.X), (int)MathF.Round(rotated.Y)));
            }
            center = origin + angle.RotateVec(new Vector2(0.5f, 1f));
            outsideWall = SEntMan.SpawnEntity("WallSolid", new EntityCoordinates(MapData.Grid.Owner, new Vector2(5.5f, 5.5f)));
            var steam = SEntMan.GetComponent<SteamTurbineComponent>(uid);
            steam.MaximumBreachChance = permitBreach ? 1f : 0f;
            steam.SteamPressure = steam.MaximumSteamPressure;
            steam.BoilerTemperature = steam.MaximumBoilerTemperature;
            var system = SEntMan.System<SteamTurbineSystem>();
            var limits = system.GetBurstAreaRestriction((uid, steam), true);
            Assert.That(limits.BreachTiles, Is.EquivalentTo(permitBreach ? footprint : new HashSet<Vector2i>()));
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(uid, "water", out var water, out _), Is.True);
            solutions.TryAddReagent(water!.Value, "Water", FixedPoint2.New(500), out _);
            SEntMan.System<DamageableSystem>().TryChangeDamage(uid,
                new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(45) } }, true);
        });
        await RunTicks(40);
        await Server.WaitAssertion(() =>
        {
            var breaches = 0;
            var damagedFloors = 0;
            for (var x = -7; x <= 7; x++)
            for (var y = -7; y <= 7; y++)
            {
                var index = new Vector2i(x, y);
                var tile = MapSystem.GetTileRef(MapData.Grid.Owner, MapData.Grid.Comp, index).Tile;
                var vacuum = tile.IsEmpty || ((Content.Shared.Maps.ContentTileDefinition)TileMan[tile.TypeId]).MapAtmosphere;
                if (vacuum)
                {
                    breaches++;
                    Assert.That(permitBreach && footprint.Contains(index), Is.True, $"Unexpected spacing at {index}.");
                }
                if (tile.TypeId != TileMan["FloorSteel"].TileId)
                    damagedFloors++;
                if (Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center) > 3.001f)
                    Assert.That(tile.TypeId, Is.EqualTo(TileMan["FloorSteel"].TileId), $"Damage escaped radius at {index}.");
            }
            Assert.That(breaches, permitBreach ? Is.InRange(1, 6) : Is.Zero);
            Assert.That(damagedFloors, Is.GreaterThan(0));
            Assert.That(SEntMan.GetComponent<DamageableComponent>(outsideWall).TotalDamage, Is.EqualTo(FixedPoint2.Zero));
        });
    }

    [Test]
    public async Task OrdinaryExplosionsKeepNativeTileDamage()
    {
        await Server.WaitAssertion(() =>
        {
            for (var x = -9; x <= 9; x++)
            for (var y = -9; y <= 9; y++)
                MapSystem.SetTile(MapData.Grid, new Vector2i(x, y), new Tile(TileMan["FloorSteel"].TileId));
            var pos = Transform.ToMapCoordinates(new EntityCoordinates(MapData.Grid.Owner, new Vector2(0.5f, 0.5f)));
            SEntMan.System<ExplosionSystem>().QueueExplosion(pos, "NFSteamPressureBurst", 4000f, 10f, 60f, null,
                tileBreakScale: 15f, maxTileBreak: 2);
        });
        await RunTicks(40);
        await Server.WaitAssertion(() =>
        {
            var tile = MapSystem.GetTileRef(MapData.Grid.Owner, MapData.Grid.Comp, new Vector2i(4, 0)).Tile;
            Assert.That(tile.IsEmpty || ((Content.Shared.Maps.ContentTileDefinition)TileMan[tile.TypeId]).MapAtmosphere, Is.True);
        });
    }

    [Test]
    public async Task FlashExtendsBeyondDamageWithoutBreakingOuterFloor()
    {
        Vector2 center = default;
        await Server.WaitAssertion(() =>
        {
            for (var x = -8; x <= 8; x++)
            for (var y = -8; y <= 8; y++)
                MapSystem.SetTile(MapData.Grid, new Vector2i(x, y), new Tile(TileMan["FloorSteel"].TileId));
            var pos = Transform.ToMapCoordinates(new EntityCoordinates(MapData.Grid.Owner, new Vector2(0.5f, 0.5f)));
            center = pos.Position;
            var explosion = SEntMan.System<ExplosionSystem>();
            explosion.QueueExplosion(pos, "NFSteamPressureBurst", explosion.RadiusToIntensity(3f, 10f, 60f),
                10f, 60f, null, tileBreakScale: 15f, maxTileBreak: 2,
                areaRestriction: new Content.Server._NF.Explosion.ExplosionAreaRestriction { DamageRadius = 3f, FlashRadius = 4.5f });
        });
        await RunTicks(3);
        await Server.WaitAssertion(() =>
        {
            var found = false;
            var outerVisualTiles = 0;
            var query = SEntMan.EntityQueryEnumerator<Content.Shared.Explosion.Components.ExplosionVisualsComponent>();
            while (query.MoveNext(out var visual))
            {
                if (visual.ExplosionType != "NFSteamPressureBurst" || !visual.Tiles.TryGetValue(MapData.Grid.Owner, out var gridTiles))
                    continue;
                found = true;
                foreach (var index in gridTiles.Values.SelectMany(tiles => tiles))
                {
                    var distance = Vector2.Distance(MapSystem.GridTileToWorldPos(MapData.Grid.Owner, MapData.Grid.Comp, index), center);
                    Assert.That(distance, Is.LessThanOrEqualTo(4.501f));
                    if (distance <= 3.001f)
                        continue;
                    outerVisualTiles++;
                    Assert.That(MapSystem.GetTileRef(MapData.Grid.Owner, MapData.Grid.Comp, index).Tile.TypeId,
                        Is.EqualTo(TileMan["FloorSteel"].TileId));
                }
            }
            Assert.That(found, Is.True);
            Assert.That(outerVisualTiles, Is.GreaterThan(0));
        });
    }

    [TestCase("Solid", "solid")]
    [TestCase("Liquid", "liquid")]
    public async Task SupplyLampsTrackRealContents(string moduleKind, string expectedKind)
    {
        await SpawnTarget("NFStationaryGeneratorSteamStandard" + moduleKind + "Empty");
        await AssertIndicator(GeneratorStatusVisuals.FuelKind, expectedKind);
        await AssertIndicator(GeneratorStatusVisuals.FuelLevel, "empty");
        await AssertIndicator(GeneratorStatusVisuals.WaterLevel, "empty");
        await Server.WaitAssertion(() =>
        {
            var uid = STarget!.Value;
            var module = SEntMan.System<FuelModuleSystem>().GetInstalledModule(uid)!.Value;
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            if (moduleKind == "Liquid")
            {
                Assert.That(solutions.TryGetSolution(module, "tank", out var tank, out _), Is.True);
                solutions.TryAddReagent(tank!.Value, "WeldingFuel", FixedPoint2.New(1500), out _);
            }
            else
                SEntMan.GetComponent<Content.Shared._NF.Power.FuelModules.FuelModuleComponent>(module).FractionalFuel["Plasma"] = 12000;
            Assert.That(solutions.TryGetSolution(uid, "water", out var water, out _), Is.True);
            solutions.TryAddReagent(water!.Value, "Water", FixedPoint2.New(250), out _);
            var steam = SEntMan.GetComponent<SteamTurbineComponent>(uid);
            steam.BoilerTemperature = 700;
            steam.SteamPressure = 1.1f;
        });
        await AssertIndicator(GeneratorStatusVisuals.FuelLevel, "full");
        await AssertIndicator(GeneratorStatusVisuals.WaterLevel, "full");
        await AssertIndicator(GeneratorStatusVisuals.TemperatureLevel, "normal");
        await Server.WaitAssertion(() =>
        {
            var steam = SEntMan.GetComponent<SteamTurbineComponent>(STarget!.Value);
            steam.BoilerTemperature = 980;
            steam.SteamPressure = 1.8f;
        });
        await AssertIndicator(GeneratorStatusVisuals.PressureLevel, "high");
        await AssertIndicator(GeneratorStatusVisuals.TemperatureLevel, "high");
        await Client.WaitAssertion(() =>
        {
            var sprite = CEntMan.GetComponent<SpriteComponent>(CTarget!.Value);
            var index = CEntMan.System<SpriteSystem>().LayerMapGet((CTarget.Value, sprite), GeneratorStatusLayers.FuelLabel);
            Assert.That(sprite.AllLayers.ElementAt(index).RsiState.ToString(), Is.EqualTo("fuel_" + expectedKind));
            index = CEntMan.System<SpriteSystem>().LayerMapGet((CTarget.Value, sprite), GeneratorStatusLayers.TemperatureLamp);
            Assert.That(sprite.AllLayers.ElementAt(index).Color, Is.EqualTo(Color.FromHex("#FF544A")));
        });
        await Interact(Screw, Pry);
        await AssertIndicator(GeneratorStatusVisuals.FuelKind, "none");
    }

    [TestCase("Compact", "empty1", "full1")]
    [TestCase("Standard", "empty2", "left2")]
    public async Task IsotopePanelShowsCellsAndShielding(string size, string empty, string occupied)
    {
        await SpawnTarget("NFStationaryGeneratorIsotope" + size + "Empty");
        await AssertIndicator(GeneratorStatusVisuals.IsotopeBays, "closed");
        await Interact(Screw);
        await AssertIndicator(GeneratorStatusVisuals.IsotopeBays, empty);
        await InteractUsing("NFIsotopeCellCasing");
        await AssertIndicator(GeneratorStatusVisuals.IsotopeBays, occupied);
        await InteractUsing("NFRadiationShieldingInsertR2");
        await AssertIndicator(GeneratorStatusVisuals.IsotopeBays, "shielded");
        await Client.WaitAssertion(() =>
        {
            var sprite = CEntMan.GetComponent<SpriteComponent>(CTarget!.Value);
            var index = CEntMan.System<SpriteSystem>().LayerMapGet((CTarget.Value, sprite), GeneratorStatusLayers.IsotopeBays);
            Assert.That(sprite.AllLayers.ElementAt(index).RsiState.ToString(), Is.EqualTo("bays_shielded"));
        });
        await Interact(Pry);
        await AssertIndicator(GeneratorStatusVisuals.IsotopeBays, occupied);
        await Interact(Screw);
        await AssertIndicator(GeneratorStatusVisuals.IsotopeBays, "closed");
    }

    private async Task AssertIndicator(GeneratorStatusVisuals key, string expected)
    {
        await RunTicks(5);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.System<SharedAppearanceSystem>().TryGetData<string>(STarget!.Value, key, out var value), Is.True);
            Assert.That(value, Is.EqualTo(expected));
        });
    }
}
