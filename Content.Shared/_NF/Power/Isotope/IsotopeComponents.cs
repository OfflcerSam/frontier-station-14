// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Prototypes;

namespace Content.Shared._NF.Power.Isotope;

public enum IsotopeCellState : byte
{
    Empty, Loading, Loaded, Active, Depleted,
}

[RegisterComponent]
public sealed partial class IsotopeCellComponent : Component
{
    [DataField] public IsotopeCellState State;
    [DataField] public ProtoId<IsotopeFuelPrototype>? FuelType;
    [DataField] public int LoadedMaterial;
    [DataField] public float RemainingLifetime;
    // Residual activity persists when a depleted casing is reloaded.
    [DataField] public float DepletedRadiation;
    [DataField] public float NextVariation;
    [DataField] public float CurrentVariation = 1f;
}

[RegisterComponent]
public sealed partial class IsotopeGeneratorComponent : Component
{
    [DataField] public List<string> CellSlots = new() { "cell1" };
    [DataField] public float CapacitorRating = 1f;
}

/// <summary>Reusable host shielding and tool-serviced insert/contents slots.</summary>
[RegisterComponent]
public sealed partial class RadiationShieldingComponent : Component
{
    [DataField] public string ShieldingSlot = "shielding";
    [DataField] public List<string> CellSlots = new() { "cell1" };
    [DataField] public float BaselineResistance = 0.5f;
}

[RegisterComponent]
public sealed partial class RadiationShieldingInsertComponent : Component
{
    [DataField(required: true)] public float Resistance;
    [DataField(required: true)] public int Rating;
}

