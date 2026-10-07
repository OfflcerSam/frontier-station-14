// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Content.Server._NF.Power.FuelModules;

/// <summary>Liquid loss at damage steps, expressed as fractions of destruction damage and tank capacity.</summary>
[RegisterComponent]
public sealed partial class FuelLeakComponent : Component
{
    [DataField] public string SolutionName = "tank";
    [DataField] public float LeakDamageFraction = 0.20f;
    [DataField] public float LeakDamageStep = 0.02f;
    [DataField] public float LeakCapacityFractionPerStep = 0.02f;
}
