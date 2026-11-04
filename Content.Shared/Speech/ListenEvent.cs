using Content.Shared._Starlight.Language; // Frontier: Starlight language support
namespace Content.Shared.Speech;

public sealed class ListenEvent : EntityEventArgs
{
    public readonly string Message;
    public readonly EntityUid Source;
    public readonly LanguagePrototype? Language; // Frontier

    public ListenEvent(string message, EntityUid source, LanguagePrototype? language = null) // Frontier
    {
        Message = message;
        Source = source;
        Language = language; // Frontier
    }
}

public sealed class ListenAttemptEvent : CancellableEntityEventArgs
{
    public readonly EntityUid Source;

    public ListenAttemptEvent(EntityUid source)
    {
        Source = source;
    }
}
