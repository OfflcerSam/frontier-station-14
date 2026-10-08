// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
#nullable enable
using System.Numerics;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._NF.Power.Components;
using Content.Server._NF.Power.EntitySystems;
using Content.Server._NF.Power.FuelModules;
using Content.Server._NF.Power.Steam;
using Content.Server.Atmos.EntitySystems;
using Content.Server.NodeContainer.EntitySystems;
using Content.Server.NodeContainer.Nodes;
using Content.Server.Power.Generator;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Content.Shared.Power.Generator;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Utility;
using Content.Server.Temperature.Components;

namespace Content.IntegrationTests.Tests._NF.Power;

public sealed class CommercialSteamRevisionTests : InteractionTest
{
    [Test]
    public async Task TwoPortsPreserveMixedAirAndDisconnect()
    {
        EntityUid machine = default;
        EntityUid firstExhaust = default;
        EntityUid secondExhaust = default;
        EntityUid firstIntake = default;
        EntityUid secondIntake = default;
        await Server.WaitAssertion(() =>
        {
            for (var x = -3; x < 7; x++)
            for (var y = -3; y < 7; y++)
                MapSystem.SetTile(MapData.Grid, new Vector2i(x, y), new Tile(TileMan[Plating].TileId));
            machine = SEntMan.SpawnEntity("NFStationaryGeneratorSteamCommercialEmpty", new EntityCoordinates(MapData.Grid.Owner, new Vector2(0.5f, 0.5f)));
            firstExhaust = SEntMan.SpawnEntity("GasPipeStraight", new EntityCoordinates(MapData.Grid.Owner, new Vector2(0.5f, 3.5f)));
            secondExhaust = SEntMan.SpawnEntity("GasPipeStraight", new EntityCoordinates(MapData.Grid.Owner, new Vector2(1.5f, 3.5f)));
            firstIntake = SEntMan.SpawnEntity("GasPipeStraight", new EntityCoordinates(MapData.Grid.Owner, new Vector2(0.5f, -0.5f)));
            secondIntake = SEntMan.SpawnEntity("GasPipeStraight", new EntityCoordinates(MapData.Grid.Owner, new Vector2(1.5f, -0.5f)));
        });
        await RunTicks(10);
        await Server.WaitAssertion(() =>
        {
            var system = SEntMan.System<GeneratorPipingSystem>();
            var component = SEntMan.GetComponent<GeneratorPipingComponent>(machine);
            var exhausts = system.GetConnectedPorts((machine, component), true);
            var intakes = system.GetConnectedPorts((machine, component), false);
            Assert.That(exhausts.Count, Is.EqualTo(2));
            Assert.That(intakes.Count, Is.EqualTo(2));
            foreach (var air in exhausts) { air.Clear(); air.Temperature = 293.15f; }
            foreach (var air in intakes)
            {
                air.Clear();
                air.Temperature = 293.15f;
                air.SetMoles(Gas.Oxygen, 10f);
                air.SetMoles(Gas.Nitrogen, 30f);
            }
            Assert.That(system.TryConsumeIntake(machine, 2f), Is.True);
            foreach (var air in intakes)
            {
                Assert.That(air.GetMoles(Gas.Oxygen), Is.EqualTo(9f).Within(0.001f));
                Assert.That(air.GetMoles(Gas.Nitrogen), Is.EqualTo(27f).Within(0.001f));
            }
            foreach (var air in exhausts)
                Assert.That(air.GetMoles(Gas.Nitrogen), Is.EqualTo(3f).Within(0.001f));
            var text = SEntMan.System<Content.Shared.Examine.ExamineSystemShared>().GetExamineText(machine, SPlayer).ToString();
            Assert.That(text, Does.Contain("air intake gauge reads"));
            Assert.That(system.TryConsumeIntake(machine, 100f), Is.False);
            Assert.That(intakes[0].GetMoles(Gas.Nitrogen), Is.EqualTo(27f).Within(0.001f));
            SEntMan.DeleteEntity(firstExhaust);
            SEntMan.DeleteEntity(firstIntake);
        });
        await RunTicks(5);
        await Server.WaitAssertion(() =>
        {
            var system = SEntMan.System<GeneratorPipingSystem>();
            var component = SEntMan.GetComponent<GeneratorPipingComponent>(machine);
            Assert.That(system.GetConnectedPorts((machine, component), true).Count, Is.EqualTo(1));
            Assert.That(system.GetConnectedPorts((machine, component), false).Count, Is.EqualTo(1));
            var burn = new GeneratorBeforeFuelBurnEvent(0.01f);
            SEntMan.EventBus.RaiseLocalEvent(machine, ref burn);
            Assert.That(burn.Cancelled, Is.False);
            Transform.Unanchor(machine);
            Assert.That(system.TryGetExhaustMixture(machine, out _), Is.False);
            Assert.That(component.Connections, Is.Empty);
        });
    }

    [TestCase(10f)]
    [TestCase(500f)]
    public async Task EscapedSteamHeatsNearbyHuman(float releasedWater)
    {
        EntityUid gridUid = default;
        EntityUid machine = default;
        EntityUid human = default;
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.System<MapLoaderSystem>().TryLoadGrid(MapId,
                new ResPath("Maps/Test/Breathing/3by3-20oxy-80nit.yml"), out var grid), Is.True);
            gridUid = grid!.Value.Owner;
            foreach (var wall in SEntMan.System<EntityLookupSystem>().GetEntitiesInRange<Content.Server.Atmos.Components.AirtightComponent>(
                         new EntityCoordinates(gridUid, Vector2.Zero), 6f))
                SEntMan.DeleteEntity(wall.Owner);
            for (var x = -4; x <= 4; x++)
            for (var y = -4; y <= 4; y++)
            {
                MapSystem.SetTile(gridUid, SEntMan.GetComponent<Robust.Shared.Map.Components.MapGridComponent>(gridUid),
                    new Vector2i(x, y), new Tile(TileMan["FloorSteel"].TileId));
                if (Math.Abs(x) == 4 || Math.Abs(y) == 4)
                    SEntMan.SpawnEntity("WallSolid", new EntityCoordinates(gridUid, new Vector2(x + 0.5f, y + 0.5f)));
            }
            machine = SEntMan.SpawnEntity("NFStationaryGeneratorSteamCommercialEmpty", new EntityCoordinates(gridUid, new Vector2(0.5f, 0.5f)));
            human = SEntMan.SpawnEntity("MobHuman", new EntityCoordinates(gridUid, new Vector2(-0.5f, 1.5f)));
        });
        await RunTicks(100);
        await Server.WaitAssertion(() =>
        {
            var atmos = SEntMan.System<AtmosphereSystem>();
            foreach (var air in atmos.GetAllMixtures(gridUid, true))
            {
                if (air.Immutable) continue;
                air.Clear();
                air.Temperature = 293.15f;
                air.SetMoles(Gas.Oxygen, 21f);
                air.SetMoles(Gas.Nitrogen, 83f);
            }
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(machine, "water", out var water, out _), Is.True);
            solutions.TryAddReagent(water!.Value, "Water", FixedPoint2.New(releasedWater), out _);
            var temperature = SEntMan.GetComponent<TemperatureComponent>(human);
            temperature.CurrentTemperature = 310.15f;
            var steam = SEntMan.GetComponent<SteamTurbineComponent>(machine);
            steam.BoilerTemperature = 982.15f;
            if (releasedWater >= 500f)
                SEntMan.System<SteamTurbineSystem>().RuptureSteam((machine, steam), false);
            else
                SEntMan.System<SteamTurbineSystem>().EmitSteam((machine, steam), releasedWater);
            var airAtHuman = atmos.GetContainingMixture(human)!;
            Assert.That(airAtHuman.Temperature, Is.GreaterThan(330f));
            Assert.That(airAtHuman.Temperature, Is.LessThanOrEqualTo(982.16f));
            Assert.That(temperature.CurrentTemperature, Is.GreaterThan(temperature.HeatDamageThreshold),
                "Steam contact must heat a nearby exposed body, with no direct damage injection.");
            // Use the real atmos exposure event and real temperature protections, without blast/fire damage.
            var transform = SEntMan.GetComponent<TransformComponent>(human);
            var exposure = new AtmosExposedUpdateEvent(transform.Coordinates, airAtHuman, transform);
            SEntMan.EventBus.RaiseLocalEvent(human, ref exposure);
            Assert.That(temperature.CurrentTemperature, Is.GreaterThan(310.15f));
            Assert.That(SEntMan.System<SteamTurbineSystem>().GetWaterLevel((machine, steam)), Is.Zero);
            var before = airAtHuman.Temperature;
            Assert.That(SEntMan.System<SteamTurbineSystem>().EmitSteam((machine, steam), releasedWater), Is.Zero);
            Assert.That(airAtHuman.Temperature, Is.EqualTo(before), "An empty boiler cannot release more heat.");
        });
        await RunTicks(180);
        await Server.WaitAssertion(() =>
        {
            var temperature = SEntMan.GetComponent<TemperatureComponent>(human);
            TestContext.Out.WriteLine($"Release {releasedWater}u: body {temperature.CurrentTemperature} K after 3 seconds.");
            Assert.That(SEntMan.GetComponent<Content.Shared.Damage.DamageableComponent>(human).Damage.DamageDict["Heat"],
                Is.GreaterThan(FixedPoint2.Zero), "Actual temperature damage must occur after steam contact.");
            if (releasedWater >= 500f)
            {
                Assert.That(temperature.CurrentTemperature, Is.GreaterThan(temperature.HeatDamageThreshold));
                Assert.That(SEntMan.GetComponent<Content.Shared.Damage.DamageableComponent>(human).Damage.DamageDict["Heat"],
                    Is.GreaterThan(FixedPoint2.Zero));
            }
        });
    }

    [TestCase("CombustionStandard")]
    [TestCase("StirlingCompact")]
    [TestCase("StirlingCompactSolid")]
    [TestCase("StirlingCompactLiquid")]
    [TestCase("StirlingStandard")]
    [TestCase("StirlingStandardSolid")]
    [TestCase("StirlingStandardLiquid")]
    [TestCase("SteamStandard")]
    [TestCase("SteamStandardSolid")]
    [TestCase("SteamStandardLiquid")]
    [TestCase("SteamCommercial")]
    [TestCase("SteamCommercialSolid")]
    [TestCase("SteamCommercialLiquid")]
    [TestCase("IsotopeCompact")]
    [TestCase("IsotopeStandard")]
    public async Task MappingSuppliesAndEmptyConstruction(string variant)
    {
        EntityUid gridUid = default;
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.System<MapLoaderSystem>().TryLoadGrid(MapId,
                new ResPath("Maps/Test/Breathing/3by3-20oxy-80nit.yml"), out var grid), Is.True);
            gridUid = grid!.Value.Owner;
            foreach (var wall in SEntMan.System<EntityLookupSystem>().GetEntitiesInRange<Content.Server.Atmos.Components.AirtightComponent>(
                         new EntityCoordinates(gridUid, Vector2.Zero), 10f))
                SEntMan.DeleteEntity(wall.Owner);
            for (var x = -4; x <= 9; x++)
            for (var y = -4; y <= 9; y++)
            {
                MapSystem.SetTile(gridUid, SEntMan.GetComponent<Robust.Shared.Map.Components.MapGridComponent>(gridUid),
                    new Vector2i(x, y), new Tile(TileMan["FloorSteel"].TileId));
                if (x == -4 || x == 9 || y == -4 || y == 9)
                    SEntMan.SpawnEntity("WallSolid", new EntityCoordinates(gridUid, new Vector2(x + 0.5f, y + 0.5f)));
            }
        });
        await RunTicks(100);
        EntityUid mapped = default;
        EntityUid empty = default;
        await Server.WaitAssertion(() =>
        {
            foreach (var air in SEntMan.System<AtmosphereSystem>().GetAllMixtures(gridUid, true))
            {
                if (air.Immutable) continue;
                air.Clear();
                air.Temperature = 293.15f;
                air.SetMoles(Gas.Oxygen, 21f);
                air.SetMoles(Gas.Nitrogen, 83f);
            }
            SEntMan.SpawnEntity("GasPipeStraight", new EntityCoordinates(gridUid, new Vector2(0.5f, 3.5f)));
            mapped = SEntMan.SpawnEntity("NFStationaryGenerator" + variant, new EntityCoordinates(gridUid, new Vector2(0.5f, 0.5f)));
            empty = SEntMan.SpawnEntity("NFStationaryGenerator" + variant + "Empty", new EntityCoordinates(gridUid, new Vector2(5.5f, 0.5f)));
            var environment = SEntMan.System<AtmosphereSystem>().GetContainingMixture(mapped, false, true);
            if (environment is { Immutable: false })
                environment.SetMoles(Gas.Oxygen, 100f);
            if (!variant.StartsWith("Isotope"))
            {
                Assert.That(SEntMan.System<GeneratorSystem>().GetFuel(mapped), Is.GreaterThan(0f));
                Assert.That(SEntMan.GetComponent<FuelGeneratorComponent>(mapped).On, Is.True);
                Assert.That(SEntMan.System<GeneratorSystem>().GetFuel(empty), Is.Zero);
                Assert.That(SEntMan.GetComponent<FuelGeneratorComponent>(empty).On, Is.False);
            }
            var baseVariant = variant.Replace("Solid", "").Replace("Liquid", "");
            var board = SEntMan.SpawnEntity("NFStationaryGenerator" + baseVariant + "MachineCircuitboard",
                new EntityCoordinates(gridUid, new Vector2(6.5f, 0.5f)));
            Assert.That(SEntMan.GetComponent<Content.Shared.Construction.Components.MachineBoardComponent>(board).Prototype.Id,
                Is.EqualTo("NFStationaryGenerator" + baseVariant + "Empty"));
            if (variant.StartsWith("Steam"))
            {
                var steam = SEntMan.GetComponent<SteamTurbineComponent>(mapped);
                Assert.That(steam.BoilerTemperature, Is.EqualTo(steam.RatedSteamTemperature));
                Assert.That(SEntMan.System<SteamTurbineSystem>().GetWaterLevel((mapped, steam)), Is.EqualTo(steam.WaterCapacity));
                var emptySteam = SEntMan.GetComponent<SteamTurbineComponent>(empty);
                Assert.That(emptySteam.SteamPressure, Is.Zero);
                Assert.That(SEntMan.System<SteamTurbineSystem>().GetWaterLevel((empty, emptySteam)), Is.Zero);
            }
            if (variant.StartsWith("Isotope"))
            {
                var slots = SEntMan.System<Content.Shared.Containers.ItemSlots.ItemSlotsSystem>();
                var component = SEntMan.GetComponent<Content.Shared._NF.Power.Isotope.IsotopeGeneratorComponent>(mapped);
                foreach (var slot in component.CellSlots)
                {
                    Assert.That(slots.GetItemOrNull(mapped, slot), Is.Not.Null);
                    Assert.That(slots.GetItemOrNull(empty, slot), Is.Null);
                }
            }
        });
        await RunTicks(10);
        await Server.WaitAssertion(() =>
        {
            var supplier = SEntMan.GetComponent<Content.Server.Power.Components.PowerSupplierComponent>(mapped);
            Assert.That(supplier.Enabled, Is.True,
                $"variant {variant}, oxygen {SEntMan.System<GeneratorPipingSystem>().GetAvailableOxygen(mapped)}, on {SEntMan.TryGetComponent<FuelGeneratorComponent>(mapped, out var fuel) && fuel.On}");
            Assert.That(supplier.MaxSupply, Is.GreaterThan(0f));
        });
    }

    [TestCase("Water")]
    [TestCase("WeldingFuel")]
    public async Task DebugSupplies(string reagent)
    {
        await Server.WaitAssertion(() =>
        {
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            var container = SEntMan.SpawnEntity("NFDebug" + reagent + "Tank",
                new EntityCoordinates(MapData.Grid.Owner, new Vector2(0.5f, 0.5f)));
            Assert.That(solutions.TryGetSolution(container, "tank", out _, out var tank), Is.True);
            Assert.That(tank!.GetTotalPrototypeQuantity(reagent), Is.EqualTo(FixedPoint2.New(5000)));
            var jug = SEntMan.SpawnEntity("NFDebug" + reagent + "Jug",
                new EntityCoordinates(MapData.Grid.Owner, new Vector2(0.5f, 0.5f)));
            Assert.That(solutions.TryGetDrainableSolution(jug, out _, out var jugContents), Is.True);
            Assert.That(jugContents!.GetTotalPrototypeQuantity(reagent), Is.EqualTo(FixedPoint2.New(5000)));
            Assert.That(SEntMan.GetComponent<Content.Shared.Chemistry.Components.SolutionTransferComponent>(jug).TransferAmount,
                Is.EqualTo(FixedPoint2.New(500)));
            foreach (var size in new[] { "Standard", "Commercial" })
            {
                var machine = SEntMan.SpawnEntity("NFStationaryGeneratorSteam" + size + "Debug" + reagent,
                    new EntityCoordinates(MapData.Grid.Owner, new Vector2(3.5f, 3.5f)));
                var steam = SEntMan.GetComponent<SteamTurbineComponent>(machine);
                Assert.That(SEntMan.System<SteamTurbineSystem>().GetWaterLevel((machine, steam)), Is.EqualTo(steam.WaterCapacity));
                Assert.That(SEntMan.System<GeneratorSystem>().GetFuel(machine),
                    reagent == "Water" ? Is.Zero : Is.GreaterThan(0f));
            }
        });
    }

    [TestCase("GasPipeStraightAlt1")]
    [TestCase("GasPipeStraightAlt2")]
    public async Task PortsRequirePrimaryLayer(string pipePrototype)
    {
        EntityUid machine = default;
        EntityUid otherLayer = default;
        await Server.WaitAssertion(() =>
        {
            for (var x = -2; x <= 3; x++)
            for (var y = -2; y <= 5; y++)
                MapSystem.SetTile(MapData.Grid, new Vector2i(x, y), new Tile(TileMan[Plating].TileId));
            machine = SEntMan.SpawnEntity("NFStationaryGeneratorSteamCommercialEmpty",
                new EntityCoordinates(MapData.Grid.Owner, new Vector2(0.5f, 0.5f)));
            otherLayer = SEntMan.SpawnEntity(pipePrototype,
                new EntityCoordinates(MapData.Grid.Owner, new Vector2(0.5f, 3.5f)));
        });
        await RunTicks(10);
        await Server.WaitAssertion(() =>
        {
            var system = SEntMan.System<GeneratorPipingSystem>();
            Assert.That(system.TryGetExhaustMixture(machine, out _), Is.False);
            Assert.That(SEntMan.HasComponent<AtmosPipeLayersComponent>(machine), Is.False);
            SEntMan.DeleteEntity(otherLayer);
            SEntMan.SpawnEntity("GasPipeStraight",
                new EntityCoordinates(MapData.Grid.Owner, new Vector2(1.5f, 3.5f)));
        });
        await RunTicks(10);
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.System<GeneratorPipingSystem>().TryGetExhaustMixture(machine, out _), Is.True,
                "The right-hand primary-layer outlet is sufficient on its own."));
    }
}
