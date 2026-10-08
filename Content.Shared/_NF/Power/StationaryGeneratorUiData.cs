// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using Robust.Shared.Serialization;
namespace Content.Shared._NF.Power;

[Serializable, NetSerializable]
public sealed class StationaryGeneratorUiData
{
    public float CurrentPower, RatedPower, ElectricalLoad, RampRate;
    public float? TargetRpm, RatedRpm, DriveTemperature;
    public float MechanicalLoad;
    public string DriveKind = "engine";
    public string GeneratorState = "off";
    public string DriveState = "off";
    public string FuelState = "danger";
    public string WaterState = "danger";
    public string BoilerState = "cold";
    public bool HasPipedExhaust;
    public bool HasExhaust;
    public string FuelKind = "uninstalled";
    public float FuelQuantity;
    public float? FuelCapacity;
    public float FuelRate, OxygenRate, ExhaustRate, WaterRate, AveragePower;
    public float? WaterCapacity;
    public List<string> Warnings = new();
    public float? Rpm;
    public float? OxygenMoles;
    public float? ExhaustPressure;
    public float? BoilerTemperature;
    public float? BoilerPressure;
    public float? Water;
    public string OxygenState = "off";
    public string ExhaustState = "off";
    public string RotorState = "off";
    public string SafetyState = "ready";
    public bool Tripped;
}
