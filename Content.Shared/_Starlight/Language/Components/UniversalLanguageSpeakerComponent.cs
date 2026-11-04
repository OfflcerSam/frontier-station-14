// Ported from Starlight: language knowledge, translation, and speech obfuscation.
using Robust.Shared.GameStates;

namespace Content.Shared._Starlight.Language.Components;

/// <summary>
/// Frontier: understands all languages and gains universally understood speech.
/// Applies to such entities as ghosts.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class UniversalLanguageSpeakerComponent : Component;
