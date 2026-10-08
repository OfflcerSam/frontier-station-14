// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared.Chemistry.Reagent;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Server._NF.Power.Steam;

/// <summary>Boiler, condenser, and pressure state for a self-contained steam turbine.</summary>
[RegisterComponent]
public sealed partial class SteamTurbineComponent : Component
{
    // Finite release energy per unit of lost water, separate from routine exhaust.
    // Tunable gameplay approximation of stored thermal energy; never heats air above the boiler.
    [DataField] public float SteamReleaseHeat = 120000f;
    [DataField] public float SteamLeakRadius = 1.75f;
    [DataField] public float SteamRuptureRadius = 2.5f;
    [DataField] public Vector2 ReleaseOffset = new(0.5f, 0.5f);
    [DataField] public string WaterSolution = "water";
    [DataField] public float WaterCapacity = 250f;
    [DataField] public float WaterLossRate = 250f / 14400f;
    [DataField] public float RatedSteamTemperature = 650f;
    [DataField] public float BoilingTemperature = 373.15f;
    [DataField] public float CoolingRate = 0.05f;
    [DataField] public float HeatPerFuelUnit = 107f;
    [DataField] public float MaximumSteamPressure = 1.8f;
    [DataField] public float MaximumBoilerTemperature = 1000f;
    // Damage diameter is 1.5x / 2x the chassis' longest side; flash diameter is 2x / 3x.
    [DataField] public float RatedBurstRadius = 1.5f;
    [DataField] public float MaximumBurstRadius = 2f;
    [DataField] public float RatedFlashRadius = 2f;
    [DataField] public float MaximumFlashRadius = 3f;
    [DataField] public float RatedBreachChance = 0.25f;
    [DataField] public float MaximumBreachChance = 0.5f;
    [DataField] public float BurstIntensitySlope = 8f;
    [DataField] public float BurstMaximumIntensity = 40f;
    [DataField] public bool CanBreachHull;
    [DataField] public float RatedPressureKpa = 3000f;
    [DataField] public float RuptureDamageFraction = 0.25f;
    [DataField] public float PressureRecoveryRate = 0.1f;
    [DataField] public float PressureLeakRate = 0.15f;
    [DataField] public float CasingHeatPower = 1000f;
    [DataField] public float VaporMolesPerUnit = 2.88f;
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
