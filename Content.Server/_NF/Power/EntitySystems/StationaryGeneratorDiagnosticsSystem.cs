// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using Content.Server._NF.Power.Components;
using Content.Server._NF.Power.Generator;
using Content.Server._NF.Power.FuelModules;
using Content.Server._NF.Power.Steam;
using Content.Server.Power.Components;
using Content.Server.Power.Generator;
using Content.Shared._NF.Power;
using Content.Shared._NF.Power.FuelModules;
using Content.Shared.Atmos.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Power.Generator;

namespace Content.Server._NF.Power.EntitySystems;

/// <summary>One source for stationary UI diagnostics and casing status lamps.</summary>
public sealed class StationaryGeneratorDiagnosticsSystem : EntitySystem
{
    [Dependency] private readonly GeneratorPipingSystem _piping = default!;
    [Dependency] private readonly TurbineRotorSystem _rotors = default!;
    [Dependency] private readonly SteamTurbineSystem _steam = default!;
    [Dependency] private readonly GeneratorSystem _generators = default!;
    [Dependency] private readonly FuelModuleSystem _modules = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!;

    public StationaryGeneratorUiData? GetData(EntityUid uid)
    {
        if (!TryComp<StationaryGeneratorComponent>(uid, out var stationary) ||
            !TryComp<FuelGeneratorComponent>(uid, out var generator))
            return null;
        var data = new StationaryGeneratorUiData();
        var critical = false;
        void Warn(string warning, bool severe = false)
        {
            data.Warnings.Add(warning);
            data.SafetyState = warning;
            critical |= severe;
        }
        var running = generator.On;
        data.RatedPower = stationary.RatedPower;
        data.MechanicalLoad = running ? generator.TargetPower / Math.Max(stationary.RatedPower, 1f) : 0f;
        if (TryComp<PowerSupplierComponent>(uid, out var supplier))
        {
            data.CurrentPower = supplier.Enabled && supplier.Net != null ? Math.Max(0f, supplier.CurrentSupply) : 0f;
            data.RampRate = supplier.SupplyRampRate;
            data.ElectricalLoad = supplier.Enabled && supplier.MaxSupply > 0f ? data.CurrentPower / supplier.MaxSupply : 0f;
        }
        data.HasExhaust = HasComp<GeneratorExhaustGasComponent>(uid) || HasComp<GeneratorPipingComponent>(uid) || HasComp<PipedGasFuelComponent>(uid);
        if (TryComp<GeneratorTelemetryComponent>(uid, out var telemetry))
        {
            data.FuelRate = telemetry.FuelRate;
            data.OxygenRate = telemetry.OxygenRate;
            data.ExhaustRate = telemetry.ExhaustRate;
            data.WaterRate = telemetry.WaterRate;
            data.AveragePower = telemetry.Output.AverageWatts;
        }
        var demand = running && generator.TargetPower > 0f
            ? generator.OptimalBurnRate / SharedGeneratorSystem.CalcFuelEfficiency(generator.TargetPower, generator.OptimalPower, generator) : 0f;
        var isGas = TryComp<PipedGasFuelComponent>(uid, out var gas);
        var module = _modules.GetInstalledModule(uid);
        if (isGas)
        {
            data.FuelKind = "gas";
            data.FuelQuantity = _generators.GetFuel(uid);
            var gasDemand = running ? telemetry is { HasRateSample: true } ? telemetry.FuelRate : demand : 0f;
            data.FuelState = SupplyState(data.FuelQuantity, gasDemand);
        }
        else if (module is { } installed && TryComp<FuelModuleComponent>(installed, out var fuelModule))
        {
            data.FuelKind = fuelModule.Kind == FuelModuleKind.Liquid ? "liquid" : "solid";
            data.FuelQuantity = _modules.GetPhysicalFuel(installed);
            data.FuelCapacity = fuelModule.BaseCapacity * (1f + 0.2f * (fuelModule.MatterBinRating - 1f));
            if (fuelModule.Kind == FuelModuleKind.Liquid && _solutions.TryGetSolution(installed, "tank", out _, out var fuel))
                data.FuelCapacity = fuel.MaxVolume.Float();
            data.FuelState = QuantityState(data.FuelQuantity, data.FuelCapacity.Value);
        }
        if (data.FuelState == "danger")
            Warn(data.FuelKind == "uninstalled" ? "module" : "fuel", true);
        else if (data.FuelState == "warning")
            Warn("fuel-low");

        if (stationary.OxygenMolesPerFuelUnit > 0f || isGas)
        {
            data.OxygenMoles = _piping.GetAvailableOxygen(uid);
            var oxygenDemand = running ? telemetry is { HasRateSample: true } ? telemetry.OxygenRate :
                demand * (isGas ? gas!.OxygenPerMole : stationary.OxygenMolesPerFuelUnit) : 0f;
            data.OxygenState = SupplyState(data.OxygenMoles.Value, oxygenDemand);
            if (data.OxygenState == "danger")
                Warn("oxygen", true);
            else if (data.OxygenState == "warning")
                Warn("oxygen-low");
        }
        if (TryComp<GeneratorPipingComponent>(uid, out var piping))
        {
            data.HasPipedExhaust = true;
            if (_piping.TryGetExhaustMixture(uid, out var exhaust))
            {
                data.ExhaustPressure = exhaust.Pressure;
                data.ExhaustState = exhaust.Pressure >= piping.ExhaustShutdownPressure ? "danger" :
                    exhaust.Pressure > piping.ExhaustThrottlePressure ? "warning" : "normal";
            }
            else
                data.ExhaustState = "danger";
        }
        else if (data.HasExhaust)
            data.ExhaustState = _piping.GetIntakeMixture(uid) != null ? "normal" : "danger";
        if (data.ExhaustState == "danger")
            Warn("exhaust", true);
        else if (data.ExhaustState == "warning")
            Warn("exhaust-low");
        if (isGas && data.FuelQuantity > 0f && data.OxygenMoles > 0f && data.ExhaustState != "danger" &&
            !EntityManager.System<PipedGasFuelSystem>().CanBurn((uid, gas!), 0.001f))
            Warn("supply", true);

        if (TryComp<SteamTurbineComponent>(uid, out var steam))
        {
            data.BoilerTemperature = steam.BoilerTemperature;
            data.BoilerPressure = steam.SteamPressure * steam.RatedPressureKpa;
            data.Water = _steam.GetWaterLevel((uid, steam));
            data.WaterCapacity = _solutions.TryGetSolution(uid, steam.WaterSolution, out _, out var water)
                ? water.MaxVolume.Float() : steam.WaterCapacity;
            data.WaterState = QuantityState(data.Water.Value, data.WaterCapacity.Value);
            data.BoilerState = BoilerState(running, steam.BoilerTemperature, steam.BoilingTemperature,
                steam.SteamPressure, steam.PressureTripThreshold, data.Water.Value);
            if (data.WaterState == "danger")
                Warn("water", true);
            else if (data.WaterState == "warning")
                Warn("water-low");
            if (data.BoilerState == "heating")
                Warn("heating");
        }
        if (!Transform(uid).Anchored)
            Warn("unanchored");
        if (_generators.GetIsClogged(uid))
            Warn("clogged", true);
        if (TryComp<FlammableComponent>(uid, out var flame) && flame.OnFire ||
            module is { } burningModule && TryComp<FlammableComponent>(burningModule, out var fuelFlame) && fuelFlame.OnFire)
            Warn("fire", true);
        var damage = _rotors.DamageFraction(uid);
        if (damage >= 0.2f)
            Warn("damage");
        if (TryComp<GeneratorDriveComponent>(uid, out var drive))
        {
            data.DriveTemperature = drive.WorkingTemperature;
            data.Rpm = drive.EngineRpm;
            data.TargetRpm = GeneratorDriveSystem.EngineTargetRpm(drive, generator);
            data.RatedRpm = drive.MaximumOperatingRpm;
        }
        if (TryComp<TurbineRotorComponent>(uid, out var rotor))
        {
            data.DriveKind = "turbine";
            data.Rpm = rotor.Rpm;
            data.TargetRpm = TurbineRotorSystem.TargetRpm(rotor, generator);
            data.RatedRpm = rotor.MaximumOperatingRpm;
            data.Tripped = rotor.Tripped;
            data.RotorState = rotor.Rpm >= rotor.MaximumOperatingRpm * rotor.TripRatio ? "danger" :
                rotor.Rpm >= rotor.MaximumOperatingRpm * rotor.WarningRatio ? "warning" : rotor.Rpm > 0f ? "normal" : "off";
            if (damage >= 0.4f)
                Warn("limited");
            if (rotor.Rpm >= rotor.MaximumOperatingRpm * rotor.WarningRatio)
                Warn("overspeed");
            if (rotor.Rpm >= rotor.MaximumOperatingRpm * rotor.GovernorRatio)
                Warn("governor", true);
            if (rotor.Tripped)
                Warn("tripped", true);
        }
        data.GeneratorState = data.Tripped ? "danger" : !running ? "off" : critical ? "danger" : data.Warnings.Count > 0 ? "warning" : "normal";
        data.DriveState = data.GeneratorState;
        return data;
    }

    public static string QuantityState(float amount, float capacity) =>
        amount <= 0f ? "danger" : amount < capacity * 0.25f ? "warning" : "normal";

    public static string SupplyState(float amount, float demand) =>
        amount <= 0f ? "danger" : amount < demand ? "warning" : "normal";

    public static string BoilerState(bool running, float temperature, float boiling, float pressure, float usablePressure, float water) =>
        running ? temperature >= boiling && pressure >= usablePressure && water > 0f ? "operational" : "heating" :
        temperature >= boiling ? "cooling" : "cold";
}
