// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Content.Server._NF.Power.Generator;

[RegisterComponent]
public sealed partial class CombustionShrapnelComponent : Component
{
    [DataField] public int HitFragments = 3;
    [DataField] public int DestructionFragments = 6;
    [DataField] public float CatastrophicHitFraction = 0.25f;
    [DataField] public System.Numerics.Vector2 ReleaseOffset = new(0, 0.5f);
    [DataField] public bool HitBurstUsed;
    [DataField] public bool DestructionBurstUsed;
}

[RegisterComponent]
public sealed partial class GeneratorShrapnelComponent : Component
{
    public System.Numerics.Vector2 Origin;
    public bool Launched;
    [DataField] public float MaximumRange = 3f;
}
