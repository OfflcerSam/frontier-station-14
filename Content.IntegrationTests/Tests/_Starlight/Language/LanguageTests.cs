using System.Linq;
using Content.Client.UserInterface.Systems.Chat;
using Content.Server.Chat.Systems;
using Content.Server.Radio;
using Content.Server.Radio.EntitySystems;
using Content.Server._DV.TapeRecorder;
using Content.Shared._DV.TapeRecorder;
using Content.Shared._Starlight.Language;
using Content.Shared._Starlight.Language.Components;
using Content.Shared._Starlight.Language.Systems;
using Content.Shared.Chat;
using Content.Shared.Cloning;
using Content.Shared.Administration;
using Content.Shared.Eye.Blinding.Systems;
using Content.Shared.Speech.Muting;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Radio;
using Robust.Client.UserInterface;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._Starlight.Language;

/// <summary>
/// Covers language knowledge, per-listener radio privacy, prefix selection, and cloning.
/// </summary>
[TestFixture]
public sealed class LanguageTests
{
    [Test]
    public async Task LocalSpeechPrivacyAndVisualRestrictions()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var (server, client) = pair;
        var map = await pair.CreateTestMap();
        EntityUid speaker = default;
        EntityUid listener = default;
        EntityUid relay = default;
        EntityUid? previous = null;
        var messages = 0;
        var netSpeaker = NetEntity.Invalid;
        var controller = client.ResolveDependency<IUserInterfaceManager>().GetUIController<ChatUIController>();

        await server.WaitPost(() =>
        {
            speaker = server.EntMan.SpawnEntity("MobReptilian", map.MapCoords);
            listener = server.EntMan.SpawnEntity("MobHuman", map.MapCoords);
            previous = pair.Player!.AttachedEntity;
            server.PlayerMan.SetAttachedEntity(pair.Player, listener);
            netSpeaker = server.EntMan.GetNetEntity(speaker);
        });
        await pair.RunTicksSync(5);

        await server.WaitPost(() => server.System<ChatSystem>().TrySendInGameICMessage(speaker,
            "+drcPrivate words.", InGameICChatType.Speak, ChatTransmitRange.Normal));
        await pair.RunTicksSync(5);
        await client.WaitAssertion(() =>
        {
            var received = controller.History.Where(h => h.Msg.SenderEntity == netSpeaker).ToList();
            Assert.That(received, Has.Count.EqualTo(1));
            Assert.That(received[0].Msg.Message, Is.Not.EqualTo("Private words."));
            Assert.That(received[0].Msg.WrappedMessage, Does.Not.Contain("Private words."));
        });

        await server.WaitPost(() =>
        {
            var languages = server.System<SharedLanguageSystem>();
            languages.AddLanguage(speaker, "Sign");
            languages.AddLanguage(listener, "Sign");
            languages.SetLanguage(speaker, "Sign");
            server.EntMan.AddComponent<MutedComponent>(speaker);
            server.System<ChatSystem>().TrySendInGameICMessage(speaker,
                "Visible gestures.", InGameICChatType.Speak, ChatTransmitRange.Normal);
        });
        await pair.RunTicksSync(5);
        await client.WaitAssertion(() =>
        {
            var received = controller.History.Where(h => h.Msg.SenderEntity == netSpeaker).ToList();
            Assert.That(received.Last().Msg.Message, Is.EqualTo("Visible gestures."), "Muted speakers can sign.");
            messages = received.Count;
        });

        await server.WaitPost(() =>
        {
            server.System<BlindableSystem>().SetMinDamage(listener, 9);
            var chat = server.System<ChatSystem>();
            chat.TrySendInGameICMessage(speaker, "Hidden gestures.", InGameICChatType.Speak, ChatTransmitRange.Normal);
            chat.TrySendInGameICMessage(speaker, "Hidden whisper.", InGameICChatType.Whisper, ChatTransmitRange.Normal);
        });
        await pair.RunTicksSync(5);
        await client.WaitAssertion(() => Assert.That(
            controller.History.Count(h => h.Msg.SenderEntity == netSpeaker), Is.EqualTo(messages),
            "Blind listeners must not receive signed speech or whispers."));

        var netRelay = NetEntity.Invalid;
        await server.WaitPost(() =>
        {
            // Trusted relays have no EmotingComponent, but can reproduce authorized nonverbal sounds.
            relay = server.EntMan.SpawnEntity(null, map.MapCoords);
            netRelay = server.EntMan.GetNetEntity(relay);
            var languages = server.System<SharedLanguageSystem>();
            languages.AddLanguage(listener, "Chittin");
            server.System<ChatSystem>().TrySendInGameICMessage(relay, "Recorded chittering.",
                InGameICChatType.Speak, ChatTransmitRange.Normal, checkRadioPrefix: false,
                ignoreActionBlocker: true, languageOverride: server.ProtoMan.Index<LanguagePrototype>("Chittin"));
        });
        await pair.RunTicksSync(5);
        await client.WaitAssertion(() => Assert.That(
            controller.History.Last(h => h.Msg.SenderEntity == netRelay).Msg.Message,
            Is.EqualTo("Recorded chittering.")));

        await server.WaitPost(() =>
        {
            var languages = server.System<SharedLanguageSystem>();
            languages.AddLanguage(speaker, "Chittin");
            languages.SetLanguage(speaker, "Chittin");
            server.System<MobStateSystem>().ChangeMobState(speaker, MobState.Critical);
            server.System<ChatSystem>().TrySendInGameICMessage(speaker,
                "Incapacitated speech.", InGameICChatType.Speak, ChatTransmitRange.Normal);
            server.System<MobStateSystem>().ChangeMobState(speaker, MobState.Alive);
            server.System<AdminFrozenSystem>().FreezeAndMute(speaker);
            server.System<ChatSystem>().TrySendInGameICMessage(speaker,
                "Frozen speech.", InGameICChatType.Speak, ChatTransmitRange.Normal);
        });
        await pair.RunTicksSync(5);
        await client.WaitAssertion(() => Assert.That(
            controller.History.Count(h => h.Msg.SenderEntity == netSpeaker), Is.EqualTo(messages),
            "Nonverbal languages must respect action restrictions."));

        await server.WaitPost(() =>
        {
            server.PlayerMan.SetAttachedEntity(pair.Player!, previous);
            server.EntMan.DeleteEntity(speaker);
            server.EntMan.DeleteEntity(listener);
            server.EntMan.DeleteEntity(relay);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task KnowledgePrefixesAndCloning()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var languages = server.System<SharedLanguageSystem>();
            var human = entMan.SpawnEntity("MobHuman", map.MapCoords);
            var lizard = entMan.SpawnEntity("MobReptilian", map.MapCoords);
            var ai = entMan.SpawnEntity("StationAiBrain", map.MapCoords);
            Assert.Multiple(() =>
            {
                Assert.That(languages.CanSpeak(human, "SolCommon"), Is.True);
                Assert.That(languages.CanSpeak(human, "Draconic"), Is.False);
                Assert.That(languages.CanSpeak(lizard, "Draconic"), Is.True);
                Assert.That(languages.CanUnderstand(human, "Draconic"), Is.False);
                Assert.That(languages.CanSpeak(ai, "Machine"), Is.True);
                Assert.That(languages.CanUnderstand(ai, "Draconic"), Is.True);
            });

            languages.AddLanguage(human, "Draconic");
            languages.AddLanguage(human, "Sign");
            var selected = languages.GetLanguage(human).ID;
            var text = "+DRCSecret words";
            Assert.That(languages.GetLanguageFromPrefix(human, ref text, out var parsed, true).ID, Is.EqualTo("Draconic"));
            Assert.That(parsed, Is.True);
            Assert.That(text, Is.EqualTo("Secret words"));
            Assert.That(languages.GetLanguage(human).ID, Is.EqualTo(selected), "One-shot prefixes must not change the selection.");

            languages.AddLanguage(human, "Cat");
            languages.AddLanguage(human, "CatFeral");
            text = "+catfMeow";
            Assert.That(languages.GetLanguageFromPrefix(human, ref text, out parsed, true).ID, Is.EqualTo("CatFeral"));
            Assert.That(parsed, Is.True);
            Assert.That(text, Is.EqualTo("Meow"));

            languages.SetLanguage(human, "Draconic");
            var clone = entMan.SpawnEntity("MobHuman", map.MapCoords);
            server.System<SharedCloningSystem>().CloneComponents(human, clone, server.ProtoMan.Index<CloningSettingsPrototype>("Body"));
            Assert.That(languages.CanSpeak(clone, "Draconic"), Is.True);
            Assert.That(languages.CanSpeak(clone, "Sign"), Is.True);
            Assert.That(languages.GetLanguage(clone).ID, Is.EqualTo("Draconic"));

            languages.RemoveLanguage(human, "Draconic");
            Assert.That(languages.CanSpeak(human, "Draconic"), Is.False);
            Assert.That(languages.GetLanguage(human).ID, Is.Not.EqualTo("Draconic"));
            Assert.That(languages.CanSpeak(clone, "Draconic"), Is.True, "Clones must have independent language lists.");

            // Repeated obfuscation must preserve punctuation and be stable within the round.
            var draconic = server.ProtoMan.Index<LanguagePrototype>("Draconic");
            var unknown = languages.ObfuscateSpeech("Secret words!", draconic);
            Assert.That(unknown, Is.EqualTo(languages.ObfuscateSpeech("Secret words!", draconic)));
            Assert.That(unknown, Does.EndWith("!"));
            Assert.That(unknown, Is.Not.EqualTo("Secret words!"));

            entMan.DeleteEntity(human);
            entMan.DeleteEntity(lizard);
            entMan.DeleteEntity(ai);
            entMan.DeleteEntity(clone);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RadioSelectsVersionForListener()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var human = server.EntMan.SpawnEntity("MobHuman", map.MapCoords);
            var lizard = server.EntMan.SpawnEntity("MobReptilian", map.MapCoords);
            var language = server.ProtoMan.Index<LanguagePrototype>("Draconic");
            var channel = server.ProtoMan.Index<RadioChannelPrototype>("Common");
            var original = new MsgChatMessage { Message = new ChatMessage(ChatChannel.Radio, "secret", "secret", NetEntity.Invalid, null) };
            var obfuscated = new MsgChatMessage { Message = new ChatMessage(ChatChannel.Radio, "unknown", "unknown", NetEntity.Invalid, null) };
            var ev = new RadioReceiveEvent("secret", lizard, channel, lizard, original, language, obfuscated);
            var radio = server.System<RadioSystem>();

            Assert.That(radio.GetChatMessageForListener(human, ev), Is.SameAs(obfuscated));
            Assert.That(radio.GetChatMessageForListener(lizard, ev), Is.SameAs(original));
            var recording = new TapeCassetteRecordedMessage(0, "speaker", "Default", "secret") { Language = "Draconic" };
            var recorder = server.System<TapeRecorderSystem>();
            Assert.That(recorder.GetTranscriptMessage(recording, human), Does.Not.Contain("secret"));
            Assert.That(recorder.GetTranscriptMessage(recording, lizard), Is.EqualTo("secret"));
            server.System<SharedLanguageSystem>().AddLanguage(human, "Draconic", addSpoken: false);
            Assert.That(radio.GetChatMessageForListener(human, ev), Is.SameAs(original));
            Assert.That(original.Message.Message, Is.EqualTo("secret"), "Selecting a listener's version must not mutate the original.");

            server.EntMan.DeleteEntity(human);
            server.EntMan.DeleteEntity(lizard);
        });
        await pair.CleanReturnAsync();
    }
}
