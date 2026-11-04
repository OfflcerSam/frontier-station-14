using Content.Server.Chat.Systems;
using Content.Shared._Starlight.Language;
using Content.Shared._Starlight.Language.Components;
using Content.Shared._Starlight.Language.Systems;
using Content.Shared.Chat;
using Content.Shared.Ghost;
using Content.Shared.Eye.Blinding.Components;
using Content.Shared.Hands.Components;
using Content.Shared.Speech;
using Content.Shared.Radio;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Server.Chat.Systems;

public sealed partial class ChatSystem
{
    [Dependency] private readonly SharedLanguageSystem _languages = default!;

    /// <summary>
    /// Speech restrictions belong to the language: signing needs hands and interaction instead of a voice.
    /// </summary>
    private bool CanUseLanguage(EntityUid source, LanguagePrototype language, bool ignoreActionBlocker)
    {
        return (ignoreActionBlocker || (language.Speech.RequireSpeech
                   ? _actionBlocker.CanSpeak(source)
                   : _actionBlocker.CanEmote(source))) &&
               (!language.Speech.RequireHands ||
                TryComp<HandsComponent>(source, out var hands) && hands.Count > 0 && _actionBlocker.CanInteract(source, null));
    }

    /// <summary>
    /// Visual speech needs sight as well as an unobstructed path. Observers can follow it while spectating.
    /// </summary>
    private bool CanSeeLanguage(EntityUid source, EntityUid listener, float range)
    {
        return HasComp<GhostComponent>(listener) ||
               ((!TryComp<BlindableComponent>(listener, out var blind) || !blind.IsBlind) &&
                _examineSystem.InRangeUnOccluded(source, listener, range));
    }

    private bool UnderstandsLanguage(EntityUid listener, LanguagePrototype language)
    {
        // Observers retain access to the original message for spectating and moderation.
        return HasComp<GhostComponent>(listener) || _languages.CanUnderstand(listener, language.ID);
    }

    /// <summary>
    /// Hive languages reach entities that understand them without radio hardware or a local audible message.
    /// </summary>
    private void SendLanguageHivemind(EntityUid source, string message, LanguagePrototype language,
        ProtoId<RadioChannelPrototype> channelId)
    {
        var channel = _prototypeManager.Index(channelId);
        var name = FormattedMessage.EscapeText(Name(source));
        var verb = language.Speech.SpeechVerbOverrides is { Count: > 0 } overrides
            ? Loc.GetString(_random.Pick(overrides))
            : Loc.GetString(_random.Pick(GetSpeechVerb(source, message).SpeechVerbStrings));
        var wrapped = Loc.GetString("chat-radio-message-wrap",
            ("color", channel.Color), ("fontType", "Default"), ("fontSize", 12),
            ("verb", verb),
            ("channel", $"\\[{channel.LocalizedName}\\]"), ("name", name),
            ("message", FormatLanguageMessage(language, FormattedMessage.EscapeText(message))));

        foreach (var session in _playerManager.Sessions)
        {
            if (session.AttachedEntity is not { } listener || !UnderstandsLanguage(listener, language))
                continue;
            _chatManager.ChatMessageToOne(ChatChannel.Radio, message, wrapped, source, false, session.Channel);
        }

        _replay.RecordServerMessage(new ChatMessage(ChatChannel.Radio, message, wrapped, GetNetEntity(source), null));
        _adminLogger.Add(Content.Shared.Database.LogType.Chat, Content.Shared.Database.LogImpact.Low,
            $"Hivemind ({language.ID}) from {ToPrettyString(source):user}: {message}");
    }

    /// <summary>
    /// Styles only the message body. User text must be escaped before it reaches this method.
    /// </summary>
    public string FormatLanguageMessage(LanguagePrototype language, string escapedMessage, bool obfuscated = false)
    {
        if (language.Speech.FontId is { } font && (!language.Speech.ObfuscationFont.GetValueOrDefault() || obfuscated))
            escapedMessage = $"[font={font} size={language.Speech.FontSize ?? 12}]{escapedMessage}[/font]";
        if (language.Speech.Color is { } color)
            escapedMessage = $"[color={color.ToHex()}]{escapedMessage}[/color]";

        if (_languages.GetLanguageIcon(language, obfuscated))
            escapedMessage = $"[languageicon id={language.Icon}] {escapedMessage}";
        return escapedMessage;
    }

    private string WrapLanguageSpeech(LanguagePrototype language, InGameICChatType type, string name,
        string message, SpeechVerbPrototype? speech = null, bool obfuscated = false)
    {
        var wrap = type == InGameICChatType.Whisper
            ? "chat-manager-entity-whisper-wrap-message"
            : speech?.Bold == true ? "chat-manager-entity-say-bold-wrap-message" : "chat-manager-entity-say-wrap-message";
        if (language.Speech.MessageWrapOverrides.TryGetValue((LanguageChatType)type, out var customWrap))
            wrap = customWrap;

        var verb = language.Speech.SpeechVerbOverrides is { Count: > 0 } overrides
            ? Loc.GetString(_random.Pick(overrides))
            : speech?.SpeechVerbStrings is { Count: > 0 } verbs ? Loc.GetString(_random.Pick(verbs)) : string.Empty;
        return Loc.GetString(wrap,
            ("entityName", name),
            ("verb", verb),
            ("fontType", speech?.FontId ?? "Default"),
            ("fontSize", speech?.FontSize ?? 12),
            ("color", language.Speech.Color ?? Color.White),
            ("message", FormatLanguageMessage(language, FormattedMessage.EscapeText(message), obfuscated)));
    }
}
