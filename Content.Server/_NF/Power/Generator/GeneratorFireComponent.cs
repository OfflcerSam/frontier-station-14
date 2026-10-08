// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._NF.Power.Generator;

/// <summary>Damage levels at which a fueled, running generator can ignite.</summary>
[RegisterComponent]
public sealed partial class GeneratorFireComponent : Component
{
    [DataField] public float IgnitionDamageFraction = 0.2f;
    [DataField] public float SolidIgnitionDamageFraction = 0.4f;
}
