// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._NF.Power.Components;

/// <summary>External air/exhaust connections and safe backpressure limits for a fuel generator.</summary>
[RegisterComponent]
public sealed partial class GeneratorPipingComponent : Component
{
    [DataField] public string ExhaustNode = "exhaust";
    [DataField] public string IntakeNode = "intake";
    [DataField] public float ExhaustThrottlePressure = 300f;
    [DataField] public float ExhaustShutdownPressure = 600f;
}
