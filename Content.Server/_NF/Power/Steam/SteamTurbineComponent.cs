// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Chemistry.Reagent;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Server._NF.Power.Steam;

/// <summary>Boiler, condenser, and pressure state for a self-contained steam turbine.</summary>
[RegisterComponent]
public sealed partial class SteamTurbineComponent : Component
{
    [DataField] public string WaterSolution = "water";
    [DataField] public float WaterCapacity = 250f;
    [DataField] public float WaterLossRate = 250f / 14400f;
    [DataField] public float RatedSteamTemperature = 650f;
    [DataField] public float BoilingTemperature = 373.15f;
    [DataField] public float CoolingRate = 0.05f;
    [DataField] public float HeatPerFuelUnit = 107f;
    [DataField] public float MaximumSteamPressure = 1.8f;
    [DataField] public float MaximumBoilerTemperature = 1000f;
    // RadiusToIntensity(3 / 4 metres, slope 0.5, peak intensity 1.5).
    [DataField] public float RatedBurstIntensity = 14.137f;
    [DataField] public float MaximumBurstIntensity = 32.987f;
    [DataField] public float CasingHeatPower = 1000f;
    [DataField] public float VaporMolesPerUnit = 2.88f;
    [DataField] public float VentHeatMultiplier = 2f;
    [DataField] public SoundSpecifier VaporReleaseSound = new SoundCollectionSpecifier("NFSteamVaporRelease");
    [DataField] public SoundSpecifier LeakSound = new SoundCollectionSpecifier("NFSteamDamageLeak");
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
    [DataField] public float VaporSinceReleaseSound;
    [DataField] public double LastReleaseSoundTime = -10d;
    public EntityUid? VaporReleaseAudio;
    public EntityUid? LeakAudio;
    [DataField] public bool Ruptured;
}
