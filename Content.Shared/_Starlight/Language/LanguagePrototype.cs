// Ported from Starlight: language knowledge, translation, and speech obfuscation.
using Content.Shared.Chat;
using Content.Shared.Radio;
using Content.Shared.StatusIcon;
using Robust.Shared.Prototypes;

namespace Content.Shared._Starlight.Language;

[Prototype]
public sealed partial class LanguagePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// Icon of the language visible in chat/bubbles.
    /// </summary>
    [DataField]
    public ProtoId<JobIconPrototype> Icon = "LanguageIconUnknown";

    /// <summary>
    /// Show the Icon if understood.
    /// </summary>
    [DataField]
    public bool IconUnderstood = true;

    /// <summary>
    /// Show the Icon if not understood.
    /// </summary>
    [DataField]
    public bool IconNotUnderstood = true;

    /// <summary>
    ///     Obfuscation method used by this language. By default, uses <see cref="ObfuscationMethod.Default"/>.
    /// </summary>
    [DataField]
    public ObfuscationMethod Obfuscation = ObfuscationMethod.Default;

    /// <summary>
    ///     Speech overrides used for messages sent in this language.
    /// </summary>
    [DataField]
    public SpeechOverrideInfo Speech = new();

    /// <summary>
    /// Prefix used in chat to send message with this language.
    /// Leave null if you don't want this feature for some reason.
    /// </summary>
    [DataField] public string? ChatPrefix;

    #region utility
    /// <summary>
    ///     The in-world name of this language, localized.
    /// </summary>
    public string Name => Loc.GetString($"language-{ID}-name");

    /// <summary>
    ///     The in-world description of this language, localized.
    /// </summary>
    public string Description => Loc.GetString($"language-{ID}-description");
    #endregion utility
}

[DataDefinition]
public sealed partial class SpeechOverrideInfo
{
    /// <summary>
    ///     Frontier: color applied directly to the language's message text through chat markup.
    /// </summary>
    [DataField]
    public Color? Color = null;

    [DataField]
    public string? FontId;

    /// <summary>
    /// Only show the font when we Obfuscate the message (if not understood)
    /// </summary>
    [DataField]
    public bool? ObfuscationFont = false;

    [DataField]
    public int? FontSize;

    /// <summary>
    /// Frontier: suppress local speech. If RadioChannel is set, send through that language's hive channel instead.
    /// </summary>
    [DataField]
    public bool BlockSpeech = false;

    [DataField]
    public bool AllowRadio = true;

    /// <summary>
    /// Frontier: hive channel used when BlockSpeech is true. Hive delivery does not use radio hardware.
    /// </summary>
    [DataField]
    public ProtoId<RadioChannelPrototype>? RadioChannel;

    /// <summary>
    ///     If false, the entity can use this language even when it's unable to speak (i.e. muffled or muted),
    ///     and accents are not applied to messages in this language.
    /// </summary>
    [DataField]
    public bool RequireSpeech = true;

    /// <summary>
    ///     Frontier: if false, local speech requires visual reception and produces no speech noises
    ///     or microphone input.
    /// </summary>
    [DataField]
    public bool RequireSound = true;

    /// <summary>
    ///     If true, requires the entity to have usable hands and be able to interact (not be cuffed, etc).
    /// </summary>
    [DataField]
    public bool RequireHands = false;

    /// <summary>
    ///     If not null, all messages in this language will be forced to be spoken in this chat type.
    /// </summary>
    [DataField]
    public LanguageChatType? ChatTypeOverride;

    /// <summary>
    ///     Speech verb overrides. If not provided, the default ones for the entity are used.
    /// </summary>
    [DataField]
    public List<LocId>? SpeechVerbOverrides;

    /// <summary>
    ///     Overrides for different kinds chat message wraps. If not provided, the default ones are used.
    /// </summary>
    /// <remarks>
    ///     Currently, only local chat and whispers support this. Radio and emotes are unaffected.
    ///     This is horrible.
    /// </remarks>
    [DataField]
    public Dictionary<LanguageChatType, LocId> MessageWrapOverrides = new();
}

/// <summary>
/// Shared speech override values, corresponding to Frontier's server chat types.
/// </summary>
public enum LanguageChatType : byte
{
    Speak,
    Emote,
    Whisper,
}
