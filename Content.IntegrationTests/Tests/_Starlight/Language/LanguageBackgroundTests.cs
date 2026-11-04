using System.Linq;
using Content.Client.UserInterface.Controls;
using Content.Client.UserInterface.Systems.MenuBar.Widgets;
using Content.Server.Power.EntitySystems;
using Content.Server.Traits;
using Content.Shared._Starlight.Language.Components;
using Content.Shared._Starlight.Language.Systems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Input;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Preferences;
using Content.Shared.Traits;
using Robust.Client.Input;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;

namespace Content.IntegrationTests.Tests._Starlight.Language;

[TestFixture]
public sealed class LanguageBackgroundTests
{
    [Test]
    public async Task LanguageButtonUsesDefaultShortcut()
    {
        await using var pair = await PoolManager.GetServerClient();
        await pair.Client.WaitAssertion(() =>
        {
            var input = pair.Client.ResolveDependency<IInputManager>();
            Assert.That(input.TryGetKeyBinding(ContentKeyFunctions.OpenLanguageMenu, out var binding), Is.True);
            Assert.That(binding!.BaseKey, Is.EqualTo(Keyboard.Key.L));

            // Headless input cannot resolve display names; verify the actual HUD button uses the bound function.
            var menu = new GameTopMenuBar();
            Assert.That(menu.Children.OfType<MenuButton>().Count(button =>
                button.BoundKey == ContentKeyFunctions.OpenLanguageMenu), Is.EqualTo(1));
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("Foreigner", true, false)]
    [TestCase("ForeignerLight", true, true)]
    [TestCase("Iterator", false, false)]
    public async Task BackgroundTranslatorRequiresPower(string background, bool translatesSpeech, bool nativeCommonUnderstanding)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        EntityUid mob = default;
        EntityUid translator = default;
        EntityUid cell = default;

        await server.WaitAssertion(() =>
        {
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human")
                .WithTraitPreference(background, server.ProtoMan);
            Assert.That(profile.TraitPreferences, Does.Contain((Robust.Shared.Prototypes.ProtoId<TraitPrototype>)background));
            mob = server.EntMan.SpawnEntity("MobHuman", map.MapCoords);
            server.System<TraitSystem>().ApplyTraits(mob, profile);

            var hands = server.EntMan.GetComponent<HandsComponent>(mob);
            translator = server.System<SharedHandsSystem>().EnumerateHeld((mob, hands))
                .Single(uid => server.EntMan.HasComponent<HandheldTranslatorComponent>(uid));
            server.System<ItemToggleSystem>().TrySetActive(translator, false, mob, predicted: false, showPopup: false);
            var language = server.System<SharedLanguageSystem>();
            Assert.That(language.CanSpeak(mob, "GalacticCommon"), Is.False);
            Assert.That(language.CanUnderstand(mob, "GalacticCommon"), Is.EqualTo(nativeCommonUnderstanding));
            Assert.That(language.CanSpeak(mob, "ScurretSign"), Is.EqualTo(background == "Iterator"));
            Assert.That(language.CanSpeak(mob, "SolCommon"), Is.EqualTo(background != "Iterator"));
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(server.System<ItemToggleSystem>().TrySetActive(translator, true, mob, predicted: false,
                showPopup: false), Is.True);
            var language = server.System<SharedLanguageSystem>();
            Assert.That(language.CanUnderstand(mob, "GalacticCommon"), Is.True);
            Assert.That(language.CanSpeak(mob, "GalacticCommon"), Is.EqualTo(translatesSpeech));

            Assert.That(server.System<ItemSlotsSystem>().TryGetSlot(translator, "cell_slot", out var slot), Is.True);
            cell = slot!.Item!.Value;
            server.System<BatterySystem>().SetCharge(cell, 0);
        });
        await pair.RunTicksSync(90);

        await server.WaitAssertion(() =>
        {
            var language = server.System<SharedLanguageSystem>();
            Assert.That(language.CanSpeak(mob, "GalacticCommon"), Is.False);
            Assert.That(language.CanUnderstand(mob, "GalacticCommon"), Is.EqualTo(nativeCommonUnderstanding));
            server.EntMan.DeleteEntity(mob);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BackgroundPreferencesRespectConflictsAndNativeLanguage()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await server.WaitAssertion(() =>
        {
            var prototypes = server.ProtoMan;
            var iterator = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithTraitPreference("Iterator", prototypes);
            Assert.That(iterator.WithTraitPreference("Draconic", prototypes).TraitPreferences,
                Is.EquivalentTo(iterator.TraitPreferences));
            Assert.That(iterator.WithTraitPreference("Foreigner", prototypes).TraitPreferences,
                Is.EquivalentTo(iterator.TraitPreferences));

            var foreigner = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithTraitPreference("Foreigner", prototypes);
            Assert.That(foreigner.WithTraitPreference("ForeignerLight", prototypes).TraitPreferences,
                Is.EquivalentTo(foreigner.TraitPreferences));

            var commonOnly = HumanoidCharacterProfile.DefaultWithSpecies("Sheleg");
            Assert.That(commonOnly.WithTraitPreference("Foreigner", prototypes).TraitPreferences, Is.Empty);
            var learned = commonOnly.WithTraitPreference("Draconic", prototypes).WithTraitPreference("Foreigner", prototypes);
            Assert.That(learned.TraitPreferences.Count, Is.EqualTo(2));
            Assert.That(learned.WithoutTraitPreference("Draconic", prototypes).TraitPreferences, Is.Empty);
            Assert.That(commonOnly.GetValidTraits(new Robust.Shared.Prototypes.ProtoId<TraitPrototype>[] { "Foreigner" }, prototypes),
                Is.Empty);

            // Background components must run after language grants, regardless of selection order.
            var mob = server.EntMan.SpawnEntity("MobSheleg", map.MapCoords);
            server.System<TraitSystem>().ApplyTraits(mob, learned);
            Assert.That(server.System<SharedLanguageSystem>().CanSpeak(mob, "Draconic"), Is.True);
            Assert.That(server.EntMan.GetComponent<LanguageKnowledgeComponent>(mob).Speaks,
                Does.Not.Contain((Robust.Shared.Prototypes.ProtoId<Content.Shared._Starlight.Language.LanguagePrototype>)"GalacticCommon"));
            server.EntMan.DeleteEntity(mob);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ForeignerCreditsDoNotDependOnSavedTraitOrder()
    {
        await using var pair = await PoolManager.GetServerClient();
        await pair.Server.WaitAssertion(() =>
        {
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human");
            Robust.Shared.Prototypes.ProtoId<TraitPrototype>[] traits =
                ["Draconic", "Moffic", "Terrum", "Sylvan", "Foreigner"];
            Assert.That(profile.GetValidTraits(traits, pair.Server.ProtoMan), Is.EquivalentTo(traits));
            Assert.That(profile.GetValidTraits(traits.Reverse(), pair.Server.ProtoMan), Is.EquivalentTo(traits));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ChangingSpeciesRemovesPaidNativeLanguageAndInvalidBackgrounds()
    {
        await using var pair = await PoolManager.GetServerClient();
        await pair.Server.WaitAssertion(() =>
        {
            var prototypes = pair.Server.ProtoMan;
            var human = HumanoidCharacterProfile.DefaultWithSpecies("Human")
                .WithTraitPreference("Draconic", prototypes).WithTraitPreference("Moffic", prototypes);
            var lizard = human.WithSpecies("Reptilian", prototypes).WithTraitPreference("Sylvan", prototypes);
            Assert.That(lizard.TraitPreferences.Select(id => id.ToString()), Is.EquivalentTo(new[] { "Moffic", "Sylvan" }));
            Assert.That(lizard.GetAutomaticNativeLanguage(prototypes)!.Value.ToString(), Is.EqualTo("Draconic"));

            var xeno = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithTraitPreference("Xenosocialized", prototypes);
            Assert.That(xeno.WithSpecies("Sheleg", prototypes).TraitPreferences, Is.Empty);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("Human", "MobHuman", "SolCommon", "GalacticCommon")]
    [TestCase("Vulpkanin", "MobVulpkanin", "Canilunzt", "Dog")]
    [TestCase("Moth", "MobMoth", "Moffic", "Mothroach")]
    [TestCase("Felinid", "MobFelinid", "Nekomimetic", "Cat")]
    public async Task SpeciesLanguageIsAutomaticUnlessXenosocialized(string species, string prototype,
        string native, string related)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await server.WaitAssertion(() =>
        {
            var profile = HumanoidCharacterProfile.DefaultWithSpecies(species);
            var language = server.System<SharedLanguageSystem>();
            var mob = server.EntMan.SpawnEntity(prototype, map.MapCoords);
            Assert.That(profile.TraitPreferences, Is.Empty);
            Assert.That(profile.GetAutomaticNativeLanguage(server.ProtoMan)!.Value.ToString(), Is.EqualTo(native));
            Assert.That(language.CanSpeak(mob, native), Is.True);
            Assert.That(language.CanUnderstand(mob, related), Is.True);
            Assert.That(profile.WithTraitPreference(native, server.ProtoMan).TraitPreferences, Is.Empty,
                "Automatic native knowledge must not charge language-trait points.");

            var xeno = profile.WithTraitPreference("Xenosocialized", server.ProtoMan)
                .WithTraitPreference("Draconic", server.ProtoMan);
            Assert.That(xeno.TraitPreferences.Count, Is.EqualTo(2));
            Assert.That(xeno.GetAutomaticNativeLanguage(server.ProtoMan), Is.Null);
            Assert.That(xeno.WithTraitPreference(native, server.ProtoMan).TraitPreferences,
                Is.EquivalentTo(xeno.TraitPreferences), "Xenosocialized must not buy back its forgotten native language.");
            server.System<TraitSystem>().ApplyTraits(mob, xeno);
            Assert.That(language.CanSpeak(mob, native), Is.False);
            Assert.That(language.CanUnderstand(mob, native), Is.False);
            Assert.That(language.CanSpeak(mob, "GalacticCommon"), Is.True);
            Assert.That(language.CanSpeak(mob, "Draconic"), Is.True);
            Assert.That(language.CanUnderstand(mob, related), Is.True);
            server.EntMan.DeleteEntity(mob);
        });
        await pair.CleanReturnAsync();
    }
}
