// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Content.Shared._NF.Power.FuelModules;

public enum FuelModuleKind : byte { Solid, Liquid }
public enum FuelModuleSize : byte { Compact, Standard }

[RegisterComponent]
public sealed partial class FuelModuleComponent : Component
{
    [DataField] public FuelModuleKind Kind;
    [DataField] public FuelModuleSize ModuleSize;
    [DataField(required: true)] public float BaseCapacity;
    [DataField] public float MatterBinRating = 1f;
    [DataField] public Dictionary<string, float> MaterialFuelValues = new();
    // Remaining material units, including partially burned pieces, retain their original material identity.
    [DataField] public Dictionary<string, float> FractionalFuel = new();
    [DataField] public float MinimumImpactSpeed = 15f;
    [DataField] public float SpillFraction = 0.25f;
    public TimeSpan NextSpillTime;
}

[RegisterComponent]
public sealed partial class FuelModuleHostComponent : Component
{
    [DataField] public string ModuleSlot = "fuel_module";
    [DataField] public FuelModuleSize ModuleSize;
}
