// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Content.Server._NF.Power.Generator;

/// <summary>Burns externally supplied plasma; stores no fuel outside the native pipe network.</summary>
[RegisterComponent]
public sealed partial class PipedGasFuelComponent : Component
{
    [DataField] public float OxygenPerMole = 1f;
    [DataField] public float ExhaustTemperature = 600f;
    [DataField] public float CasingHeatPerMole = 5000f;
}
