// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using Robust.Shared.Prototypes;

namespace Content.Shared._NF.Power.Isotope;

[Prototype("isotopeFuel")]
public sealed partial class IsotopeFuelPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public HashSet<string> AcceptedMaterials = new();
    [DataField(required: true)] public int RequiredMaterial;
    [DataField(required: true)] public float Lifetime;
    [DataField(required: true)] public float ElectricalOutput;
    [DataField(required: true)] public float HeatOutput;
    [DataField(required: true)] public float ActiveRadiation;
    [DataField] public float DepletedRadiation = 0.15f;
    [DataField] public float RadiationSlope = 0.25f;
    [DataField] public float TaperStart = 0.75f;
    [DataField] public float TaperEndOutput = 0.4f;
    [DataField] public float OutputVariation;
    [DataField] public float VariationInterval = 10f;
}

/// <summary>Raised before reclamation, including for nested contents.</summary>
[ByRefEvent]
public record struct MaterialReclaimAttemptEvent
{
    public bool Cancelled;
}
