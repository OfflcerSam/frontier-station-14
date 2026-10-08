// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Content.Server._NF.Power.Generator;

/// <summary>Shared rotor speed, load rejection and latched governor safeties.</summary>
[RegisterComponent]
public sealed partial class TurbineRotorComponent : Component
{
    [DataField] public float MaximumOperatingRpm = 3000f;
    [DataField] public float ResponseSeconds = 5f;
    [DataField] public float WarningRatio = 1.1f;
    [DataField] public float GovernorRatio = 1.2f;
    [DataField] public float TripRatio = 1.3f;
    [DataField] public float Rpm;
    [DataField] public bool Tripped;
    public float PreviousLoad;
}
