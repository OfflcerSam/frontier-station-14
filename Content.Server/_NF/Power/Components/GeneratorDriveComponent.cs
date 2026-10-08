// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Content.Server._NF.Power.Components;

/// <summary>Observational engine speed and prime-mover working temperature. No heat or failure effects.</summary>
[RegisterComponent]
public sealed partial class GeneratorDriveComponent : Component
{
    [DataField] public float EngineRpm;
    [DataField] public float MaximumOperatingRpm = 3000f;
    [DataField] public float ResponseSeconds = 5f;
    [DataField] public float ThermalResponseSeconds = 30f;
    [DataField] public float? WorkingTemperature;
}
