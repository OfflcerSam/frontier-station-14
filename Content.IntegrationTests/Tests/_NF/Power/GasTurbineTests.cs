// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
#nullable enable
using System.Numerics;
using System.Linq;
using Content.Server.Atmos.EntitySystems;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Utility;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._NF.Power.Components;
using Content.Server._NF.Power.EntitySystems;
using Content.Server._NF.Power.Generator;
using Content.Server._NF.Power.FuelModules;
using Content.Server.Power.Components;
using Content.Server.Power.Generator;
using Content.Shared.Atmos;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Power.Generator;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._NF.Power;

public sealed class GasTurbineTests : InteractionTest
{
    [TestCase(0)]
    [TestCase(90)]
    [TestCase(180)]
    [TestCase(270)]
    public async Task RotatedSuppliesConserveMixedGasAndStopAtomically(int degrees)
    {
        EntityUid uid = default;
        EntityUid fuelPipe = default;
        await Server.WaitAssertion(() =>
        {
            for (var x = -6; x <= 6; x++)
            for (var y = -6; y <= 6; y++)
                MapSystem.SetTile(MapData.Grid, new Vector2i(x, y), new Tile(TileMan[Plating].TileId));
            var coordinates = new EntityCoordinates(MapData.Grid.Owner, new Vector2(0.5f, 0.5f));
            var angle = Angle.FromDegrees(degrees);
            uid = SEntMan.SpawnEntity("NFStationaryGeneratorGasTurbineCommercialEmpty", coordinates);
            Transform.SetLocalRotation(uid, angle);
            SEntMan.SpawnEntity("GasPipeFourway", coordinates.Offset(angle.RotateVec(new Vector2(0, 3))));
            SEntMan.SpawnEntity("GasPipeFourway", coordinates.Offset(angle.RotateVec(new Vector2(0, -1))));
            fuelPipe = SEntMan.SpawnEntity("GasPipeFourway", coordinates.Offset(angle.RotateVec(new Vector2(-1, 1))));
        });
        await RunTicks(10);
        await Server.WaitAssertion(() =>
        {
            var gas = SEntMan.System<PipedGasFuelSystem>();
            var piping = SEntMan.System<GeneratorPipingSystem>();
            var fuel = gas.GetFuelSupplies(uid);
            Assert.That(fuel.Count, Is.EqualTo(1));
            var source = fuel[0];
            var air = piping.GetIntakeMixture(uid)!;
            Assert.That(piping.TryGetExhaustMixture(uid, out var exhaust), Is.True);
            Assert.That(source, Is.Not.SameAs(air));
            Assert.That(source, Is.Not.SameAs(exhaust));
            source.Clear(); source.SetMoles(Gas.Plasma, 100); source.SetMoles(Gas.Nitrogen, 50);
            air.Clear(); air.SetMoles(Gas.Oxygen, 100); air.SetMoles(Gas.Nitrogen, 100);
            exhaust!.Clear();
            var adapter = SEntMan.GetComponent<PipedGasFuelComponent>(uid);
            var ports = SEntMan.GetComponent<GeneratorPipingComponent>(uid);
            var names = ports.FuelNodes;
            air.SetMoles(Gas.Plasma, 100f);
            Assert.That(gas.CanBurn((uid, adapter), 0.1f), Is.True);
            ports.FuelNodes = ports.IntakeNodes;
            Assert.That(gas.CanBurn((uid, adapter), 0.1f), Is.False,
                "Even sufficient mixed fuel/air must be rejected when the ports share a network.");
            ports.FuelNodes = names;
            air.SetMoles(Gas.Plasma, 0f);
            Assert.That(gas.TryBurn((uid, adapter), 1f), Is.True);
            Assert.That(source.GetMoles(Gas.Plasma), Is.EqualTo(99f).Within(0.001));
            Assert.That(source.GetMoles(Gas.Nitrogen), Is.EqualTo(49.5f).Within(0.001));
            Assert.That(air.GetMoles(Gas.Oxygen), Is.EqualTo(99f).Within(0.001));
            Assert.That(exhaust.GetMoles(Gas.CarbonDioxide), Is.EqualTo(1f).Within(0.001));
            Assert.That(exhaust.GetMoles(Gas.Nitrogen), Is.EqualTo(1.5f).Within(0.001));
            Assert.That(exhaust.Temperature, Is.GreaterThanOrEqualTo(599.9f));
            air.SetMoles(Gas.Oxygen, 0f);
            var before = source.TotalMoles;
            Assert.That(gas.TryBurn((uid, adapter), 1f), Is.False);
            Assert.That(source.TotalMoles, Is.EqualTo(before));
            air.SetMoles(Gas.Oxygen, 100f);
            exhaust.SetMoles(Gas.Nitrogen, 650 * exhaust.Volume / (Atmospherics.R * exhaust.Temperature));
            Assert.That(gas.TryBurn((uid, adapter), 1f), Is.False);
            Assert.That(source.TotalMoles, Is.EqualTo(before));
            exhaust.Clear();
            var generator = SEntMan.System<GeneratorSystem>();
            generator.SetFuelGeneratorOn(uid, true);
            generator.Update(1f);
            Assert.That(gas.GetFuel(uid), Is.EqualTo(98f).Within(0.001));
            SEntMan.DeleteEntity(fuelPipe);
        });
        await RunTicks(5);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.System<PipedGasFuelSystem>().GetFuel(uid), Is.Zero);
            Assert.That(SEntMan.GetComponent<FuelGeneratorComponent>(uid).On, Is.False);
        });
    }

    [TestCase("NFStationaryGeneratorGasTurbineCommercialEmpty")]
    [TestCase("NFStationaryGeneratorSteamStandardEmpty")]
    [TestCase("NFStationaryGeneratorSteamCommercialEmpty")]
    public async Task SharedRotorLoadRejectionAndLatchedRestart(string prototype)
    {
        await SpawnTarget(prototype);
        await Server.WaitAssertion(() =>
        {
            var uid = STarget!.Value;
            var rotor = SEntMan.GetComponent<TurbineRotorComponent>(uid);
            var fuel = SEntMan.GetComponent<FuelGeneratorComponent>(uid);
            var supplier = SEntMan.GetComponent<PowerSupplierComponent>(uid);
            var system = SEntMan.System<TurbineRotorSystem>();
            SEntMan.System<GeneratorSystem>().SetFuelGeneratorOn(uid, true);
            SEntMan.EventBus.RaiseLocalEvent(uid, new PortableGeneratorSetTargetPowerMessage(fuel.MaxTargetPower / 1000f) { Actor = SPlayer });
            system.UpdateRotor((uid, rotor), fuel, supplier, 5f);
            Assert.That(rotor.Rpm, Is.EqualTo(3000f));
            Assert.That(rotor.Tripped, Is.False, "Maximum approved power must be safe.");
            rotor.PreviousLoad = fuel.MaxTargetPower;
            system.UpdateRotor((uid, rotor), fuel, supplier, 0.01f);
            Assert.That(rotor.Tripped, Is.True);
            Assert.That(fuel.On, Is.False);
            var start = new GeneratorStartAttemptEvent();
            SEntMan.EventBus.RaiseLocalEvent(uid, ref start);
            Assert.That(start.FailureMessage, Is.Not.Null);
            system.UpdateRotor((uid, rotor), fuel, supplier, 10f);
            Assert.That(rotor.Rpm, Is.Zero);
            Assert.That(rotor.Tripped, Is.True, "Coasting down cannot restart automatically.");
        });
    }

    [Test]
    public async Task EmptyConstructionAndUpgradesCannotCreateGas()
    {
        await SpawnTarget("NFStationaryGeneratorGasTurbineCommercialEmpty");
        await Server.WaitAssertion(() =>
        {
            var uid = STarget!.Value;
            var stationary = SEntMan.GetComponent<StationaryGeneratorComponent>(uid);
            SEntMan.System<StationaryGeneratorSystem>().ApplyPartRatings((uid, stationary), 4, 4, 4);
            var fuel = SEntMan.GetComponent<FuelGeneratorComponent>(uid);
            Assert.That(fuel.MaxTargetPower, Is.EqualTo(198000f).Within(0.1));
            Assert.That(fuel.OptimalBurnRate, Is.EqualTo(0.85f).Within(0.001));
            Assert.That(SEntMan.System<PipedGasFuelSystem>().GetFuel(uid), Is.Zero);
            Assert.That(SEntMan.HasComponent<Content.Shared._NF.Power.FuelModules.FuelModuleHostComponent>(uid), Is.False);
            Assert.That(SEntMan.GetComponent<OccluderComponent>(uid).BoundingBox, Is.EqualTo(new Box2(-0.5f, -0.5f, 1.5f, 2.5f)));
        });
    }

    [Test]
    public async Task DamageLeaksOnlyCrossedLocalVolumeSteps()
    {
        EntityUid uid = default;
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.System<MapLoaderSystem>().TryLoadGrid(MapId,
                new ResPath("Maps/Test/Breathing/3by3-20oxy-80nit.yml"), out var loaded), Is.True);
            var grid = loaded!.Value.Owner;
            foreach (var meta in SEntMan.EntityQuery<MetaDataComponent>().ToArray())
                if (meta.EntityPrototype?.ID == "WallReinforced" && SEntMan.GetComponent<TransformComponent>(meta.Owner).GridUid == grid)
                    SEntMan.DeleteEntity(meta.Owner);
            uid = SEntMan.SpawnEntity("NFStationaryGeneratorGasTurbineCommercialEmpty", new EntityCoordinates(grid, new Vector2(0.5f, 0.5f)));
            SEntMan.SpawnEntity("GasPipeFourway", new EntityCoordinates(grid, new Vector2(-0.5f, 1.5f)));
        });
        await RunTicks(10);
        await Server.WaitAssertion(() =>
        {
            var gas = SEntMan.System<PipedGasFuelSystem>();
            var supply = gas.GetFuelSupplies(uid).Single();
            supply.Clear(); supply.SetMoles(Gas.Plasma, 100); supply.SetMoles(Gas.Nitrogen, 50);
            var room = SEntMan.System<AtmosphereSystem>().GetContainingMixture(uid, false, true)!;
            Assert.That(room.Immutable, Is.False);
            var plasma = room.GetMoles(Gas.Plasma);
            var damage = SEntMan.System<DamageableSystem>();
            void Hit(float amount) => damage.TryChangeDamage(uid,
                new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(amount) } }, true);
            Hit(39);
            Assert.That(supply.GetMoles(Gas.Plasma), Is.EqualTo(100));
            Hit(1);
            var leaked = 100f * 2f / supply.Volume;
            Assert.That(room.GetMoles(Gas.Plasma) - plasma, Is.EqualTo(leaked).Within(0.001));
            Assert.That(supply.GetMoles(Gas.Plasma), Is.EqualTo(100 - leaked).Within(0.001));
            var before = supply.GetMoles(Gas.Plasma);
            Hit(1);
            Assert.That(supply.GetMoles(Gas.Plasma), Is.EqualTo(before));
            Hit(3);
            Assert.That(supply.GetMoles(Gas.Plasma), Is.EqualTo(before * (1f - 2f / supply.Volume)).Within(0.001));
            Assert.That(room.GetMoles(Gas.Plasma) + supply.GetMoles(Gas.Plasma), Is.EqualTo(plasma + 100).Within(0.001));
            before = supply.GetMoles(Gas.Plasma);
            Hit(156);
            Assert.That(supply.GetMoles(Gas.Plasma), Is.EqualTo(before * (1f - Math.Min(1f, 100f / supply.Volume))).Within(0.001),
                "Destruction vents only the local 100 L equivalent, once.");
            Assert.That(room.GetMoles(Gas.Plasma) + supply.GetMoles(Gas.Plasma), Is.EqualTo(plasma + 100).Within(0.001));
        });
    }

    [TestCase("NFStationaryGeneratorGasTurbineCommercialEmpty", 200f)]
    [TestCase("NFStationaryGeneratorSteamStandardEmpty", 100f)]
    [TestCase("NFStationaryGeneratorSteamCommercialEmpty", 180f)]
    public async Task DamageGovernorLimitsAndLatches(string prototype, float hp)
    {
        await SpawnTarget(prototype);
        await Server.WaitAssertion(() =>
        {
            var uid = STarget!.Value;
            var rotor = SEntMan.GetComponent<TurbineRotorComponent>(uid);
            var damage = SEntMan.System<DamageableSystem>();
            void Hit(float amount) => damage.TryChangeDamage(uid,
                new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(amount) } }, true);
            Hit(hp * 0.4f);
            var burn = new GeneratorBeforeFuelBurnEvent(1f);
            SEntMan.EventBus.RaiseLocalEvent(uid, ref burn);
            Assert.That(burn.PowerMultiplier, Is.EqualTo(0.7f).Within(0.001));
            Assert.That(burn.FuelUsed, Is.EqualTo(0.7f).Within(0.001));
            Hit(hp * 0.2f);
            burn = new GeneratorBeforeFuelBurnEvent(1f);
            SEntMan.EventBus.RaiseLocalEvent(uid, ref burn);
            Assert.That(burn.Cancelled, Is.True);
            Assert.That(rotor.Tripped, Is.True);
            Hit(-hp * 0.4f);
            var start = new GeneratorStartAttemptEvent();
            SEntMan.EventBus.RaiseLocalEvent(uid, ref start);
            Assert.That(start.FailureMessage, Is.Not.Null, "Exactly 20% is not repaired below 20%.");
            Assert.That(rotor.Tripped, Is.True);
        });
    }

    [TestCase(0f, 0.19f, 0f)]
    [TestCase(0.19f, 0.2f, 1f)]
    [TestCase(0.2f, 0.22f, 1f)]
    [TestCase(0f, 0.4f, 11f)]
    [TestCase(0.4f, 0.1f, 0f)]
    public void SharedLeakMarks(float previous, float current, float expected) =>
        Assert.That(DamageLeakSteps.Count(previous, current, 0.2f, 0.02f), Is.EqualTo(expected));
}
