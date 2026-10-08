// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Chemistry.Reagent;
using Robust.Shared.Prototypes;

namespace Content.Server._NF.Power.Steam;

/// <summary>Boiler, condenser, and pressure state for a self-contained steam turbine.</summary>
[RegisterComponent]
public sealed partial class SteamTurbineComponent : Component
{
    [DataField] public string WaterSolution = "water";
    [DataField] public float WaterCapacity = 250f;
    [DataField] public float WaterLossRate = 250f / 14400f;
    [DataField] public float RatedSteamTemperature = 550f;
    [DataField] public float BoilingTemperature = 373.15f;
    [DataField] public float CoolingRate = 0.05f;
    [DataField] public float HeatPerFuelUnit = 77f;
    [DataField] public float MinimumStartingWaterFraction = 0.1f;
    [DataField] public float PressureTripThreshold = 0.2f;
    [DataField] public float SteamLeakDamageFraction = 0.2f;
    [DataField] public float SteamLeakDamageStep = 0.02f;
    [DataField] public float SteamLeakCapacityFractionPerStep = 0.02f;
    [DataField] public Dictionary<string, float> SolidHeatFactors = new()
    {
        ["Wood"] = 0.75f,
        ["Coal"] = 0.9f,
        ["Plasma"] = 1f,
        ["FuelGradePlasma"] = 1f,
    };
    [DataField] public Dictionary<ProtoId<ReagentPrototype>, float> LiquidHeatFactors = new()
    {
        ["WeldingFuel"] = 1f,
        ["Ethanol"] = 0.85f,
        ["Plasma"] = 1.15f,
    };

    [DataField] public float BoilerTemperature = 293.15f;
    [DataField] public float SteamPressure;
    [DataField] public bool WasPressurized;
    [DataField] public float WaterLossRemainder;
    [DataField] public bool Ruptured;
}
