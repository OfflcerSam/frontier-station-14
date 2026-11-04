// Ported from Starlight: language knowledge, translation, and speech obfuscation.
using Robust.Shared.GameStates;

namespace Content.Shared._Starlight.Language.Components;

/// <summary>
///   Frontier: a translator that works from a container directly owned by a language speaker,
///   such as a hand or pocket.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class HandheldTranslatorComponent : BaseTranslatorComponent
{
    /// <summary>
    ///   Whether interacting with this translator toggles it on and off.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool ToggleOnInteract = true;

    /// <summary>
    ///     If true, when this translator is turned on, the entities' current spoken language will
    ///     be set to the first new language added by this translator.
    /// </summary>
    /// <remarks>
    ///      This should generally be used for translators that translate speech between two languages.
    /// </remarks>
    [DataField, AutoNetworkedField]
    public bool SetLanguageOnInteract = true;
}
