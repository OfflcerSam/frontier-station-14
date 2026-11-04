// Ported from Starlight: language knowledge, translation, and speech obfuscation.
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Starlight.Language.Components;

// <summary>
//     Component that "caches" old languages a species had. useful for revert-able language changes.
// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class LanguageCacheComponent : Component
{
    /// <summary>
    /// What languages did this entity *used* to speak
    /// </summary>
    [DataField, AutoNetworkedField]
    public HashSet<ProtoId<LanguagePrototype>>? SpeakingCache;

    /// <summary>
    /// What languages did this entity *used* to understand
    /// </summary>
    [DataField, AutoNetworkedField]
    public HashSet<ProtoId<LanguagePrototype>>? UnderstandingCache;

    /// <summary>
    /// Frontier: whether the entity had <see cref="UniversalLanguageSpeakerComponent"/> when the cache was captured.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool HasUniversal;
}
