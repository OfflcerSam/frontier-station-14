using System.Linq;
using Content.Shared._Starlight.Language;
using Content.Shared._Starlight.Language.Components;
using Content.Shared._Starlight.Language.Events;
using Content.Shared._Starlight.Language.Systems;
using Robust.Shared.Player;

namespace Content.Server._Starlight.Language;

/// <summary>
/// Initializes language speakers and validates language selection requests from clients.
/// Ported from Starlight, using Frontier's existing chat and radio systems.
/// </summary>
public sealed class LanguageSystem : SharedLanguageSystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<LanguageSpeakerComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<UniversalLanguageSpeakerComponent, DetermineEntityLanguagesEvent>(OnUniversalLanguages);
        SubscribeLocalEvent<UniversalLanguageSpeakerComponent, MapInitEvent>((uid, _, _) => UpdateEntityLanguages(uid));
        SubscribeLocalEvent<UniversalLanguageSpeakerComponent, ComponentRemove>((uid, _, _) => CallLanguagesUpdate(uid));
        SubscribeNetworkEvent<LanguagesSetMessage>(OnSetLanguage);
    }

    private void OnMapInit(Entity<LanguageSpeakerComponent> ent, ref MapInitEvent args)
    {
        UpdateEntityLanguages(ent.AsNullable());
    }

    private void OnUniversalLanguages(Entity<UniversalLanguageSpeakerComponent> ent, ref DetermineEntityLanguagesEvent args)
    {
        args.SpokenLanguages.Add(UniversalPrototype);
    }

    private void OnSetLanguage(LanguagesSetMessage message, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { Valid: true } uid ||
            GetLanguagePrototype(message.CurrentLanguage) is not { } language ||
            !CanSpeak(uid, language.ID))
            return;

        SetLanguage(uid, language.ID);
    }
}
