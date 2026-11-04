// Ported from Starlight: language knowledge, translation, and speech obfuscation.
using System.Collections.Immutable;
using System.Text;
using System.Linq;
using Content.Shared._Starlight.Language.Components;
using Content.Shared._Starlight.Language.Events;
using Content.Shared.GameTicking;
using Robust.Shared.Prototypes;
using Content.Shared.Cloning.Events;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Shared._Starlight.Language.Systems;

public abstract partial class SharedLanguageSystem : EntitySystem
{
    [Dependency] private ISharedPlayerManager _player = default!;
    [Dependency] private IPrototypeManager _prototype = default!;
    [Dependency] private SharedGameTicker _ticker = default!;
    [Dependency] private IGameTiming _gameTiming = default!;

    /// <summary>
    ///     Frontier: language granted to newly sentient entities that have no intrinsic spoken language.
    /// </summary>
    public static readonly ProtoId<LanguagePrototype> FallbackLanguagePrototype = "GalacticCommon";

    /// <summary>
    ///     Frontier: universally understood speech. Use UniversalLanguageSpeakerComponent
    ///     to grant universal understanding.
    /// </summary>
    public static readonly ProtoId<LanguagePrototype> UniversalPrototype = "Universal";

    /// <summary>
    /// The prefix used to choose a language for one message, for example <c>+drcHello</c>.
    /// </summary>
    public static readonly char ChatPrefixChar = '+';

    /// <summary>
    ///     A cached instance of <see cref="UniversalPrototype"/>
    /// </summary>
    public static LanguagePrototype Universal { get; private set; } = default!;

    /// <summary>
    ///     A cached set of all languages in the game
    /// </summary>
    [ViewVariables(VVAccess.ReadOnly)]
    public ImmutableHashSet<ProtoId<LanguagePrototype>> Languages = ImmutableHashSet<ProtoId<LanguagePrototype>>.Empty;

    public override void Initialize()
    {
        Universal = _prototype.Index(UniversalPrototype);
        Languages = _prototype.EnumeratePrototypes<LanguagePrototype>()
            .Select(x => new ProtoId<LanguagePrototype>(x.ID))
            .ToImmutableHashSet();

        SubscribeLocalEvent<LanguageKnowledgeComponent, CloningEvent>(OnClone);
        SubscribeLocalEvent<LanguageKnowledgeComponent, MapInitEvent>(OnMapInit);

        SubscribeLocalEvent<AdditionalLanguageKnowledgeComponent, MapInitEvent>(OnMapInitAdditional);

        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
    }

    public void CallLanguagesUpdate(EntityUid uid)
    {
        var comp = EnsureComp<LanguageSpeakerUpdateComponent>(uid);
        comp.TargetTick = _gameTiming.CurTick.Value + 1;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<LanguageSpeakerUpdateComponent, LanguageSpeakerComponent>();
        while (query.MoveNext(out var ent, out var update, out var speaker))
        {
            if (update.TargetTick > _gameTiming.CurTick.Value)
                continue;

            UpdateEntityLanguages((ent, speaker));
            RemComp<LanguageSpeakerUpdateComponent>(ent);
        }
    }

    private void OnClone(Entity<LanguageKnowledgeComponent> ent, ref CloningEvent ev)
    {
        if (!ev.Settings.EventComponents.Contains(Factory.GetRegistration(ent.Comp.GetType()).Name))
            return;
        var clone = ev.CloneUid;
        var comp = EnsureComp<LanguageKnowledgeComponent>(ev.CloneUid);
        if (HasComp<RestoreLanguageCacheOnCloneComponent>(ent) && TryComp<LanguageCacheComponent>(ent, out var cache))
        {
            // Frontier: restore the clone's knowledge without modifying the original entity's cache or languages.
            comp.Speaks = cache.SpeakingCache?.ToList() ?? ent.Comp.Speaks.ToList();
            comp.Understands = cache.UnderstandingCache?.ToList() ?? ent.Comp.Understands.ToList();
            if (cache.HasUniversal)
                EnsureComp<UniversalLanguageSpeakerComponent>(clone);
        }
        else
        {
            comp.Speaks = ent.Comp.Speaks.ToList();
            comp.Understands = ent.Comp.Understands.ToList();
        }
        Dirty(clone, comp);
        if (TryComp<LanguageSpeakerComponent>(clone, out var speaker))
        {
            // Frontier: retain the selection if it remains valid after cloning or cache restoration.
            if (TryComp<LanguageSpeakerComponent>(ent, out var originalSpeaker))
                speaker.CurrentLanguage = originalSpeaker.CurrentLanguage;
            UpdateEntityLanguages((clone,speaker));
        }
    }

    private void OnMapInit(Entity<LanguageKnowledgeComponent> ent, ref MapInitEvent ev)
    {
        var ev2 = new LanguageKnowledgeInitEvent(ent);
        RaiseLocalEvent(ent, ref ev2 , broadcast: true);
    }

    /// <summary>
    /// Add additional languages, generally as part of a role
    /// </summary>
    private void OnMapInitAdditional(Entity<AdditionalLanguageKnowledgeComponent> ent, ref MapInitEvent ev)
    {
        if (!TryComp<LanguageKnowledgeComponent>(ent, out var langComp))
            return;

        langComp.Speaks = langComp.Speaks.Union(ent.Comp.Speaks).Distinct().ToList();
        langComp.Understands = langComp.Understands.Union(ent.Comp.Understands).Distinct().ToList();
        Dirty(ent, langComp);

        if (TryComp<LanguageSpeakerComponent>(ent, out var speaker))
            UpdateEntityLanguages((ent, speaker));
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs ev)
    {
        if (!ev.WasModified<LanguagePrototype>())
            return;

        Languages = _prototype.EnumeratePrototypes<LanguagePrototype>()
            .Select(x => new ProtoId<LanguagePrototype>(x.ID))
            .ToImmutableHashSet();
    }

    public LanguagePrototype? GetLanguagePrototype(ProtoId<LanguagePrototype> id)
    {
        _prototype.TryIndex(id, out var proto);
        return proto;
    }

    /// <summary>
    ///     Obfuscate a message using the given language.
    /// </summary>
    public string ObfuscateSpeech(string message, LanguagePrototype language)
    {
        var builder = new StringBuilder();
        language.Obfuscation.Obfuscate(builder, message, this);

        return builder.ToString();
    }

    public bool GetLanguageIcon(LanguagePrototype language, bool obfuscated)
    {
        if (!obfuscated && language.IconUnderstood)
            return true;

        if (obfuscated && language.IconNotUnderstood)
            return true;

        return false;
    }

    /// <summary>
    ///     Generates a stable pseudo-random number in the range (min, max) (inclusively) for the given seed.
    ///     One seed always corresponds to one number, however the resulting number also depends on the current round number.
    ///     This method is meant to be used in <see cref="ObfuscationMethod"/> to provide stable obfuscation.
    /// </summary>
    internal int PseudoRandomNumber(int seed, int min, int max)
    {
        // Using RobustRandom or System.Random here is a bad idea because this method can get called hundreds of times per message.
        // Each call would require us to allocate a new instance of random, which would lead to lots of unnecessary calculations.
        // Instead, we use a simple but effective algorithm derived from the C language.
        // It does not produce a truly random number, but for the purpose of obfuscating messages in an RP-based game it's more than alright.
        seed ^= (_ticker.RoundId * 127);
        var random = (seed * 1103515245) + 12345;
        // Frontier: unsigned modulo also handles int.MinValue without overflowing Math.Abs.
        return min + (int)((uint)random % (uint)(max - min + 1));
    }

    /// <summary>
    ///     Returns the LanguageSpeakerComponent of the local player entity.
    ///     Will return null if the player does not have an entity, or if the client has not yet received the component state.
    /// </summary>
    public LanguageSpeakerComponent? GetLocalSpeaker()
    {
        return CompOrNull<LanguageSpeakerComponent>(_player.LocalEntity);
    }

    public void RequestSetLanguage(ProtoId<LanguagePrototype> language)
    {
        if (GetLocalSpeaker()?.CurrentLanguage?.Equals(language) == true)
            return;

        RaiseNetworkEvent(new LanguagesSetMessage(language));
    }

    #region public api

    public bool CanUnderstand(Entity<LanguageSpeakerComponent?> ent, ProtoId<LanguagePrototype> language, bool universalcheck = true)
    {
        if (universalcheck && (language == UniversalPrototype || HasComp<UniversalLanguageSpeakerComponent>(ent)))
            return true;

        return Resolve(ent, ref ent.Comp, logMissing: false) && ent.Comp.UnderstoodLanguages.Contains(language);
    }

    public bool CanSpeak(Entity<LanguageSpeakerComponent?> ent, ProtoId<LanguagePrototype> language)
    {
        if (!Resolve(ent, ref ent.Comp, logMissing: false))
            return false;

        return ent.Comp.SpokenLanguages.Contains(language);
    }

    /// <summary>
    ///     Returns the current language of the given entity, assumes Universal if it's not a language speaker.
    /// </summary>
    public LanguagePrototype GetLanguage(Entity<LanguageSpeakerComponent?> ent)
    {
        if (!Resolve(ent, ref ent.Comp, logMissing: false)
            || string.IsNullOrEmpty(ent.Comp.CurrentLanguage)
            || !_prototype.TryIndex<LanguagePrototype>(ent.Comp.CurrentLanguage, out var proto)
        )
            return Universal;

        return proto;
    }

    /// <summary>
    /// Attempt to resolve language based off a given prefix.
    /// </summary>
    /// <param name="ent">Entity to get language from</param>
    /// <param name="input">Input to parse for prefix. Should start with <c><see cref="ChatPrefixChar"/></c>.</param>
    /// <param name="parsed">Whether the function managed to parse the prefix or not.</param>
    /// <param name="modifyText">Whether to allow this function to modify the resulting text string or not.</param>
    /// <returns></returns>
    public LanguagePrototype GetLanguageFromPrefix(Entity<LanguageSpeakerComponent?> ent, ref string input, out bool parsed, bool modifyText = false)
    {
        parsed = false;
        // Fallback if unable to get the current selected language. Selected language is used if unable to parse.
        if (!Resolve(ent, ref ent.Comp, logMissing: false)
            || string.IsNullOrEmpty(ent.Comp.CurrentLanguage)
            || !_prototype.TryIndex<LanguagePrototype>(ent.Comp.CurrentLanguage, out var proto))
            return Universal;

        // Begin parsing
        var text = input;
        if (!text.StartsWith(ChatPrefixChar)) return proto;
        text = text[1..];
        if (text.Length == 0) return proto;

        // Prefer the longest match: animal languages include overlapping prefixes such as cat and catf.
        LanguagePrototype? match = null;
        foreach (var langId in ent.Comp.SpokenLanguages)
        {
            if (!_prototype.TryIndex(langId, out var lang) || string.IsNullOrEmpty(lang.ChatPrefix) ||
                !text.StartsWith(lang.ChatPrefix, StringComparison.OrdinalIgnoreCase) ||
                (match?.ChatPrefix?.Length ?? 0) >= lang.ChatPrefix.Length)
                continue;

            match = lang;
        }

        if (match?.ChatPrefix is { } matchedPrefix)
        {
            if (modifyText)
                input = text[matchedPrefix.Length..];
            parsed = true;
            return match;
        }

        return proto;
    }

    /// <summary>
    ///     Returns the list of languages this entity can speak.
    /// </summary>
    /// <remarks>This simply returns the value of <see cref="LanguageSpeakerComponent.SpokenLanguages"/>.</remarks>
    public List<ProtoId<LanguagePrototype>> GetSpokenLanguages(EntityUid uid) => TryComp<LanguageSpeakerComponent>(uid, out var component) ? component.SpokenLanguages : [];

    /// <summary>
    ///     Returns the list of languages this entity can understand.
    /// </summary>
    /// <remarks>This simply returns the value of <see cref="LanguageSpeakerComponent.UnderstoodLanguages"/>.</remarks>
    public List<ProtoId<LanguagePrototype>> GetUnderstoodLanguages(EntityUid uid) => TryComp<LanguageSpeakerComponent>(uid, out var component) ? component.UnderstoodLanguages : [];


    public void SetLanguage(Entity<LanguageSpeakerComponent?> ent, ProtoId<LanguagePrototype> language)
    {
        if (!CanSpeak(ent, language)
            || !Resolve(ent, ref ent.Comp)
            || ent.Comp.CurrentLanguage == language)
            return;

        ent.Comp.CurrentLanguage = language;
        RaiseLocalEvent(ent, new LanguagesUpdateEvent(), true);
        Dirty(ent);
    }

    /// <summary>
    ///     Adds a new language to the respective lists of intrinsically known languages of the given entity.
    /// </summary>
    public void AddLanguage(
        EntityUid uid,
        ProtoId<LanguagePrototype> language,
        bool addSpoken = true,
        bool addUnderstood = true)
    {
        EnsureComp<LanguageKnowledgeComponent>(uid, out var knowledge);
        EnsureComp<LanguageSpeakerComponent>(uid, out var speaker);

        if (addSpoken && !knowledge.Speaks.Contains(language))
            knowledge.Speaks.Add(language);

        if (addUnderstood && !knowledge.Understands.Contains(language))
            knowledge.Understands.Add(language);

        Dirty(uid, knowledge);
        UpdateEntityLanguages((uid, speaker));
    }

    /// <summary>
    ///     Removes a language from the respective lists of intrinsically known languages of the given entity.
    /// </summary>
    public void RemoveLanguage(
        Entity<LanguageKnowledgeComponent?> ent,
        ProtoId<LanguagePrototype> language,
        bool removeSpoken = true,
        bool removeUnderstood = true)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return;

        if (removeSpoken)
            ent.Comp.Speaks.Remove(language);

        if (removeUnderstood)
            ent.Comp.Understands.Remove(language);

        Dirty(ent);
        // Refresh a speaker if present; knowledge can also be stored on entities without speech.
        UpdateEntityLanguages(ent.Owner);
    }

    /// <summary>
    ///   Ensures the given entity has a valid language as its current language.
    ///   If not, sets it to the first entry of its SpokenLanguages list, or universal if it's empty.
    /// </summary>
    /// <returns>True if the current language was modified, false otherwise.</returns>
    public bool EnsureValidLanguage(Entity<LanguageSpeakerComponent?> ent)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return false;

        if (!ent.Comp.SpokenLanguages.Contains(ent.Comp.CurrentLanguage))
        {
            ent.Comp.CurrentLanguage = ent.Comp.SpokenLanguages.FirstOrDefault(UniversalPrototype);
            RaiseLocalEvent(ent, new LanguagesUpdateEvent());
            Dirty(ent);
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Immediately refreshes the cached lists of spoken and understood languages for the given entity.
    /// </summary>
    public void UpdateEntityLanguages(Entity<LanguageSpeakerComponent?> ent)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return;

        var ev = new DetermineEntityLanguagesEvent();
        // We add the intrinsically known languages first so other systems can manipulate them easily
        if (TryComp<LanguageKnowledgeComponent>(ent, out var knowledge))
        {
            foreach (var spoken in knowledge.Speaks)
                ev.SpokenLanguages.Add(spoken);

            foreach (var understood in knowledge.Understands)
                ev.UnderstoodLanguages.Add(understood);
        }
        else
        {
            // Scripted speakers without intrinsic knowledge preserve their existing universal speech.
            ev.SpokenLanguages.Add(UniversalPrototype);
            ev.UnderstoodLanguages.Add(UniversalPrototype);
        }

        RaiseLocalEvent(ent, ref ev);

        ent.Comp.SpokenLanguages.Clear();
        ent.Comp.UnderstoodLanguages.Clear();

        ent.Comp.SpokenLanguages.AddRange(ev.SpokenLanguages);
        ent.Comp.UnderstoodLanguages.AddRange(ev.UnderstoodLanguages);

        // If EnsureValidLanguage returns true, it also raises a LanguagesUpdateEvent, so we try to avoid raising it twice in that case.
        if (!EnsureValidLanguage(ent))
            RaiseLocalEvent(ent, new LanguagesUpdateEvent());

        Dirty(ent);
    }

    #endregion
}
