using Content.Shared._NF.Medical.Prototypes;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared.Damage;
using Content.Shared.EntityTable;
using Content.Shared.Humanoid.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Server._NF.StationEvents.Prototypes;

[Prototype]
public sealed partial class DerelictThemePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public List<VesselClass> Classes = new();

    [DataField]
    public List<EntProtoId> Anchors = new();

    [DataField]
    public int Weight = 1;

    [DataField]
    public int MaxBreachWidth = 1;

    /// <summary>
    /// When set, the incident originates at the selected equipment instead of the hull.
    /// </summary>
    [DataField]
    public DamageSpecifier? EquipmentDamage;

    /// <summary>
    /// Scrap that replaces the equipment nearest a hull impact. Requires an equipment anchor.
    /// </summary>
    [DataField]
    public EntProtoId? EquipmentWreck;

    /// <summary>
    /// Rock placed on a small exterior extension of the hull next to the breach.
    /// </summary>
    [DataField]
    public EntProtoId? CrashRock;

    [DataField]
    public bool LargeImpact;

    [DataField]
    public bool ImpactTrail;

    [DataField]
    public EntProtoId? ImpactGrowth;

    [DataField]
    public float DamageControlChance;

    [DataField]
    public int ScorchedTiles;

    [DataField]
    public List<EntProtoId> Aftermath = new();

    [DataField]
    public ProtoId<RandomHumanoidSettingsPrototype>? Casualty;

    [DataField]
    public ProtoId<MedicalBountyPrototype>? CasualtyBounty;

    /// <summary>
    /// Chance of a second casualty when the scene has space and any necessary EVA stock.
    /// </summary>
    [DataField]
    public float AdditionalCasualtyChance;

    [DataField]
    public EntProtoId? DefenseWeapon;

    [DataField]
    public ProtoId<EntityTablePrototype>? LootTable;

    [DataField]
    public List<EntProtoId> Threats = new();

    [DataField]
    public int ThreatCount = 1;
}
