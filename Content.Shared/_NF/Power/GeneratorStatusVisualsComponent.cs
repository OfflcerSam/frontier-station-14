// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Serialization;

namespace Content.Shared._NF.Power;

[RegisterComponent]
public sealed partial class GeneratorStatusVisualsComponent : Component
{
}

[Serializable, NetSerializable]
public enum GeneratorStatusVisuals : byte
{
    PanelOpen,
    Damaged,
    FuelKind,
    FuelLevel,
    WaterLevel,
    PressureLevel,
    TemperatureLevel,
    IsotopeBays,
}

[Serializable, NetSerializable]
public enum GeneratorStatusLayers : byte
{
    Panel,
    Damage,
    FuelLabel,
    FuelLamp,
    WaterLabel,
    WaterLamp,
    PressureLabel,
    PressureLamp,
    TemperatureLabel,
    TemperatureLamp,
    IsotopeBays,
}
