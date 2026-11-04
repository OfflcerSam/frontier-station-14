// Ported from Starlight: language knowledge, translation, and speech obfuscation.
using Robust.Shared.Serialization;

namespace Content.Shared._Starlight.Language.Events;

/// <summary>
///     Sent from the client to the server when it needs to want to set his currentLanguage.
///     Yeah, I'm using this instead of ExecuteCommand... Better right?
///     Frontier: the server validates the sender's spoken knowledge before changing the selection.
/// </summary>
[Serializable, NetSerializable]
public sealed class LanguagesSetMessage(string currentLanguage) : EntityEventArgs
{
    public string CurrentLanguage = currentLanguage;
}
