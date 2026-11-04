using Content.Shared._Starlight.Language; // Frontier: Starlight language support
using Content.Shared.Whitelist;
using Robust.Shared.Prototypes;

namespace Content.Shared.Traits;

/// <summary>
/// Describes a trait.
/// </summary>
[Prototype]
public sealed partial class TraitPrototype : IPrototype
{
    [ViewVariables]
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// The name of this trait.
    /// </summary>
    [DataField]
    public LocId Name { get; private set; } = string.Empty;

    /// <summary>
    /// The description of this trait.
    /// </summary>
    [DataField]
    public LocId? Description { get; private set; }

    /// <summary>
    /// Don't apply this trait to entities this whitelist IS NOT valid for.
    /// </summary>
    [DataField]
    public EntityWhitelist? Whitelist;

    /// <summary>
    /// Don't apply this trait to entities this whitelist IS valid for. (hence, a blacklist)
    /// </summary>
    [DataField]
    public EntityWhitelist? Blacklist;

    /// <summary>
    /// The components that get added to the player, when they pick this trait.
    /// </summary>
    [DataField]
    public ComponentRegistry Components { get; private set; } = new(); // Frontier: allow language-only traits

    // Frontier: language skills ported from Starlight.
    /// <summary>
    /// Languages added to intrinsic spoken knowledge when this trait is applied.
    /// </summary>
    [DataField]
    public List<ProtoId<LanguagePrototype>> LanguagesSpoken = new();

    /// <summary>
    /// Languages added to intrinsic understood knowledge when this trait is applied.
    /// </summary>
    [DataField]
    public List<ProtoId<LanguagePrototype>> LanguagesUnderstood = new();

    /// <summary>
    /// Replace native language knowledge instead of adding to it, as required by Iterator.
    /// </summary>
    [DataField]
    public bool ReplaceLanguages;

    /// <summary>
    /// Forget the species' primary native language while retaining Common and separately learned languages.
    /// </summary>
    [DataField]
    public bool RemoveNativeLanguage;

    /// <summary>
    /// A background removing this language needs at least one other intrinsic or selected spoken language.
    /// </summary>
    [DataField]
    public ProtoId<LanguagePrototype>? RequireLanguageOtherThan;

    /// <summary>
    /// Mutually exclusive traits. Checked in both directions when selecting and validating preferences.
    /// </summary>
    [DataField]
    public HashSet<ProtoId<TraitPrototype>> Conflicts = new();

    public bool ConflictsWith(TraitPrototype other)
    {
        return Conflicts.Contains(other.ID) || other.Conflicts.Contains(ID);
    }
    // End Frontier

    /// <summary>
    /// Gear that is given to the player, when they pick this trait.
    /// </summary>
    [DataField]
    public EntProtoId? TraitGear;

    /// <summary>
    /// Trait Price. If negative number, points will be added.
    /// </summary>
    [DataField]
    public int Cost = 0;

    /// <summary>
    /// Adds a trait to a category, allowing you to limit the selection of some traits to the settings of that category.
    /// </summary>
    [DataField]
    public ProtoId<TraitCategoryPrototype>? Category;
}
