// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
#nullable enable
using Content.Client._NF.Power;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._NF.Power.Components;
using Content.Server._NF.Power.EntitySystems;
using Content.Server._NF.Power.FuelModules;
using Content.Server.Power.Generator;
using Content.Shared._NF.Power;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Content.Shared.Power.Generator;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.IntegrationTests.Tests._NF.Power;

public sealed class GeneratorOutputHistoryTests
{
    [Test]
    public void OutputUsesTimeWeightingAndExpiresOldDelivery()
    {
        var history = new GeneratorOutputHistory();
        history.Add(20f, 10000f);
        Assert.That(history.AverageWatts, Is.EqualTo(10000f));
        history.Add(40f, 25000f);
        Assert.That(history.AverageWatts, Is.EqualTo(20000f));
        history.Add(15f, 0f);
        Assert.That(history.AverageWatts, Is.EqualTo(17500f));
        history.Add(60f, 0f);
        Assert.That(history.AverageWatts, Is.Zero);
    }
    [Test]
    public void ThermalResponseIsIndependentOfTickSubdivision()
    {
        var once = GeneratorDriveSystem.ApproachTemperature(300f, 600f, 30f, 30f);
        var divided = 300f;
        for (var i = 0; i < 30; i++)
            divided = GeneratorDriveSystem.ApproachTemperature(divided, 600f, 1f, 30f);
        Assert.That(divided, Is.EqualTo(once).Within(0.001f));
        Assert.That(once, Is.InRange(489f, 490f));
        Assert.That(GeneratorDriveSystem.ApproachTemperature(600f, 300f, 30f, 30f), Is.InRange(410f, 411f));
    }

    [TestCase(false, 293f, 0f, "cold")]
    [TestCase(true, 293f, 0f, "heating")]
    [TestCase(true, 400f, 0.1f, "heating")]
    [TestCase(true, 400f, 0.3f, "operational")]
    [TestCase(false, 400f, 0.3f, "cooling")]
    public void BoilerDistinguishesHeatingCoolingAndOperation(bool running, float temperature, float pressure, string expected)
    {
        Assert.That(StationaryGeneratorDiagnosticsSystem.BoilerState(running, temperature, 373.15f, pressure, 0.2f, 100f), Is.EqualTo(expected));
    }

    [Test]
    public void SupplyThresholdsIncludeExactBoundaryAndEmpty()
    {
        Assert.That(StationaryGeneratorDiagnosticsSystem.QuantityState(0f, 100f), Is.EqualTo("danger"));
        Assert.That(StationaryGeneratorDiagnosticsSystem.QuantityState(24.99f, 100f), Is.EqualTo("warning"));
        Assert.That(StationaryGeneratorDiagnosticsSystem.QuantityState(25f, 100f), Is.EqualTo("normal"));
        Assert.That(StationaryGeneratorDiagnosticsSystem.SupplyState(0.9f, 1f), Is.EqualTo("warning"));
        Assert.That(StationaryGeneratorDiagnosticsSystem.SupplyState(1f, 1f), Is.EqualTo("normal"));
    }

}

public sealed class GeneratorGaugeTests : InteractionTest
{
    [Test]
    public async Task EngineReachesFullSpeedAndSpinsDownWithoutTurbineSafety()
    {
        await SpawnTarget("NFStationaryGeneratorCombustionStandardLiquidEmpty");
        await Server.WaitAssertion(() =>
        {
            var uid = STarget!.Value;
            var generator = SEntMan.GetComponent<FuelGeneratorComponent>(uid);
            var generators = SEntMan.System<GeneratorSystem>();
            SEntMan.EventBus.RaiseLocalEvent(uid, new PortableGeneratorSetTargetPowerMessage(generator.MaxTargetPower / 1000f) { Actor = SPlayer });
            generators.SetFuelGeneratorOn(uid, true);
            var system = SEntMan.System<GeneratorDriveSystem>();
            system.Update(5f);
            var drive = SEntMan.GetComponent<GeneratorDriveComponent>(uid);
            Assert.That(drive.EngineRpm, Is.EqualTo(3000f));
            Assert.That(SEntMan.HasComponent<Content.Server._NF.Power.Generator.TurbineRotorComponent>(uid), Is.False);
            generators.SetFuelGeneratorOn(uid, false);
            system.Update(2.5f);
            Assert.That(drive.EngineRpm, Is.EqualTo(1500f));
            system.Update(2.5f);
            Assert.That(drive.EngineRpm, Is.Zero);
        });
    }

    [Test]
    public async Task PhysicalConsumptionExcludesRefillingAndSpilling()
    {
        await SpawnTarget("NFStationaryGeneratorCombustionStandardLiquidEmpty");
        await Server.WaitAssertion(() =>
        {
            var host = STarget!.Value;
            var modules = SEntMan.System<FuelModuleSystem>();
            var module = modules.GetInstalledModule(host)!.Value;
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(module, "tank", out var tank, out _), Is.True);
            var telemetry = SEntMan.EnsureComponent<GeneratorTelemetryComponent>(host);
            solutions.TryAddReagent(tank!.Value, "WeldingFuel", FixedPoint2.New(100), out _);
            Assert.That(telemetry.FuelPending, Is.Zero);
            var before = modules.GetPhysicalFuel(module);
            SEntMan.EventBus.RaiseLocalEvent(host, new GeneratorUseFuel(1f));
            Assert.That(telemetry.FuelPending, Is.EqualTo(before - modules.GetPhysicalFuel(module)).Within(0.0001f));
            Assert.That(telemetry.FuelPending, Is.GreaterThan(0f));
            var consumed = telemetry.FuelPending;
            solutions.RemoveReagent(tank.Value, "WeldingFuel", FixedPoint2.New(10));
            Assert.That(telemetry.FuelPending, Is.EqualTo(consumed));
            var data = SEntMan.System<StationaryGeneratorDiagnosticsSystem>().GetData(host)!;
            Assert.That(data.FuelKind, Is.EqualTo("liquid"));
            Assert.That(data.FuelQuantity, Is.EqualTo(modules.GetPhysicalFuel(module)));
            Assert.That(data.FuelCapacity, Is.EqualTo(1500f));
        });
    }

    [Test]
    public async Task WindowHidesIrrelevantPanelsAndDisplaysEveryWarning()
    {
        await SpawnTarget("NFStationaryGeneratorGasTurbineCommercialEmpty");
        await Client.WaitAssertion(() =>
        {
            using var window = new StationaryGeneratorWindow();
            window.SetEntity(CTarget!.Value);
            var component = CEntMan.GetComponent<FuelGeneratorComponent>(CTarget.Value);
            var data = new StationaryGeneratorUiData
            {
                Rpm = 1000,
                TargetRpm = 2500,
                RatedRpm = 3000,
                CurrentPower = 55000,
                RatedPower = 110000,
                GeneratorState = "normal",
                DriveState = "normal",
                OxygenMoles = 10,
                HasPipedExhaust = true,
                HasExhaust = true,
                FuelKind = "gas",
                DriveKind = "turbine",
                DriveTemperature = 400f,
                FuelQuantity = 123.45f,
                Warnings = new() { "fuel", "exhaust", "tripped" }
            };
            var state = new PortableGeneratorComponentBuiState(component, 1f, false, null) { Stationary = data };
            state.MaximumPower = 180000;
            window.Update(state);
            Assert.That(window.FindControl<Label>("Rpm").Text, Is.EqualTo("1000 / 3000 RPM"));
            Assert.That(window.FindControl<Label>("CurrentOutput").Text, Is.EqualTo("55.00 / 180 kW"));
            Assert.That(window.FindControl<Label>("GeneratorStatus").Text, Is.EqualTo("Operational"));
            Assert.That(window.FindControl<Label>("DriveStatus").Text, Is.EqualTo("Operational"));
            Assert.That(window.FindControl<Label>("StatusLabel").Text, Is.EqualTo("Shutting Off"));
            Assert.That(window.FindControl<Label>("StatusLabel").StyleClasses, Does.Contain("Caution"));
            Assert.That(window.FindControl<Label>("NetworkStatus").Text, Is.EqualTo("Disconnected"));
            Assert.That(window.FindControl<Label>("NetworkStatus").FontColorOverride, Is.EqualTo(Robust.Shared.Maths.Color.Gray));
            foreach (var (supply, status) in new[] { (110000f, "Supplied"), (109999f, "Low"), (100000f, "Low"), (99999f, "Insufficient") })
            {
                state.NetworkStats = (100000f, supply);
                window.Update(state);
                Assert.That(window.FindControl<Label>("NetworkStats").Text, Is.EqualTo("55.00 / 100.00 kW"));
                Assert.That(window.FindControl<Label>("NetworkStatus").Text, Is.EqualTo(status));
            }
            Assert.That(window.FindControl<Control>("BoilerPanel").Visible, Is.False);
            Assert.That(window.FindControl<Control>("RpmRow").Visible, Is.True);
            Assert.That(window.FindControl<Control>("AtmosPanel").Visible, Is.True);
            Assert.That(window.FindControl<Control>("DrivePanel").Visible, Is.True);
            Assert.That(window.FindControl<Label>("DriveHeading").Text, Is.EqualTo("Turbine"));
            Assert.That(window.FindControl<Label>("Safety").Text!.Split('\n'), Has.Length.EqualTo(3));
            Assert.That(window.FindControl<Label>("FuelLeft").Text, Does.Contain("123.45"));
            Assert.That(window.FindControl<Label>("FuelLeft").Text, Does.Not.Contain("/"));
            data.BoilerTemperature = 500;
            data.BoilerPressure = 1000;
            data.BoilerState = "heating";
            data.Rpm = null;
            data.OxygenMoles = null;
            data.HasPipedExhaust = data.HasExhaust = false;
            data.Warnings.Clear();
            window.Update(state);
            Assert.That(window.FindControl<Control>("BoilerPanel").Visible, Is.True);
            Assert.That(window.FindControl<Label>("WaterStatus").Text, Is.EqualTo("Empty"));
            Assert.That(window.FindControl<Label>("BoilerStatus").Text, Is.EqualTo("Heating"));
            Assert.That(window.FindControl<Label>("BoilerStatus").StyleClasses, Does.Contain("Caution"));
            data.BoilerState = "cooling";
            window.Update(state);
            Assert.That(window.FindControl<Label>("BoilerStatus").Text, Is.EqualTo("Cooling"));
            Assert.That(window.FindControl<Label>("BoilerStatus").StyleClasses, Does.Contain("Caution"));
            Assert.That(window.FindControl<Label>("StatusLabel").Text, Is.EqualTo("Off"));
            Assert.That(window.FindControl<Label>("StatusLabel").FontColorOverride, Is.EqualTo(Robust.Shared.Maths.Color.Gray));
            Assert.That(window.FindControl<Control>("RpmRow").Visible, Is.False);
            Assert.That(window.FindControl<Control>("AtmosPanel").Visible, Is.False);
            Assert.That(window.FindControl<Control>("WarningsPanel").Visible, Is.False);
        });
    }
}
