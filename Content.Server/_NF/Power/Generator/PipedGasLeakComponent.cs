// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Content.Server._NF.Power.Generator;

[RegisterComponent]
public sealed partial class PipedGasLeakComponent : Component
{
    [DataField] public float LocalVolume = 100f;
    [DataField] public float LeakDamageFraction = 0.2f;
    [DataField] public float LeakDamageStep = 0.02f;
    [DataField] public float LeakCapacityFractionPerStep = 0.02f;
    public bool Ruptured;
}
