using System.Collections.Generic; // Frontier: apply language knowledge before background components
using Content.Shared._Starlight.Language.Systems; // Frontier: Starlight language support
using Content.Shared.GameTicking;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Roles;
using Content.Shared.Preferences; // Frontier: reusable spawn trait application
using Content.Shared.Traits;
using Content.Shared.Whitelist;
using Robust.Shared.Prototypes;

namespace Content.Server.Traits;

public sealed class TraitSystem : EntitySystem
{
    // Frontier: apply language skills using the existing trait system.
    [Dependency] private readonly SharedLanguageSystem _languages = default!;

    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly SharedHandsSystem _sharedHandsSystem = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelistSystem = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
    }

    // When the player is spawned in, add all trait components selected during character creation
    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        // Check if player's job allows to apply traits
        if (args.JobId == null ||
            !_prototypeManager.TryIndex<JobPrototype>(args.JobId ?? string.Empty, out var protoJob) ||
            !protoJob.ApplyTraits)
        {
            return;
        }

        ApplyTraits(args.Mob, args.Profile); // Frontier: shared spawn implementation for language backgrounds
    }

    // Frontier: language effects run before components so backgrounds can depend on learned languages.
    public void ApplyTraits(EntityUid mob, HumanoidCharacterProfile profile)
    {
        // Frontier: do not apply conflicting language backgrounds from invalid or older profiles.
        var traits = new List<TraitPrototype>();
        foreach (var traitId in profile.GetValidTraits(profile.TraitPreferences, _prototypeManager))
        {
            if (!_prototypeManager.TryIndex<TraitPrototype>(traitId, out var traitPrototype))
            {
                Log.Warning($"No trait found with ID {traitId}!");
                return;
            }

            if (_whitelistSystem.IsWhitelistFail(traitPrototype.Whitelist, mob) ||
                _whitelistSystem.IsBlacklistPass(traitPrototype.Blacklist, mob))
                continue;

            traits.Add(traitPrototype); // Frontier: retain the validated and whitelisted traits for both passes
        }

        // Frontier: learned languages must exist before Foreigner removes Common and configures its translator.
        foreach (var traitPrototype in traits)
        {
            // Frontier: match Xenosocialized's removal of the species language, keeping learned alternatives.
            if (traitPrototype.RemoveNativeLanguage && profile.GetSpeciesNativeLanguage(_prototypeManager) is { } native)
                _languages.RemoveLanguage(mob, native);

            // Frontier: Iterator replaces native knowledge; ordinary language skills still accumulate.
            if (traitPrototype.ReplaceLanguages)
            {
                _languages.SetLanguages(mob, traitPrototype.LanguagesSpoken, traitPrototype.LanguagesUnderstood);
            }
            else
            {
                foreach (var language in traitPrototype.LanguagesSpoken)
                    _languages.AddLanguage(mob, language, addUnderstood: false);
                foreach (var language in traitPrototype.LanguagesUnderstood)
                    _languages.AddLanguage(mob, language, addSpoken: false);
            }
            // End Frontier
        }

        // Frontier: apply components and gear only after all intrinsic language choices are known.
        foreach (var traitPrototype in traits)
        {
            // Add all components required by the prototype
            EntityManager.AddComponents(mob, traitPrototype.Components, false);

            // Add item required by the trait
            if (traitPrototype.TraitGear == null)
                continue;

            if (!TryComp(mob, out HandsComponent? handsComponent))
                continue;

            var coords = Transform(mob).Coordinates;
            var inhandEntity = Spawn(traitPrototype.TraitGear, coords);
            _sharedHandsSystem.TryPickup(mob,
                inhandEntity,
                checkActionBlocker: false,
                handsComp: handsComponent);
        }
    }
}
