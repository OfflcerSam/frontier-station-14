using Content.Server.NPC.Components;
using Content.Shared.CombatMode;
using Content.Shared.Damage;
using Content.Shared.Mobs.Components;
using Content.Shared.NPC.Systems;
using Robust.Shared.Map; // Frontier
using Robust.Shared.Prototypes; // Frontier
using Robust.Shared.Timing;

namespace Content.Server.NPC.Systems;

/// <summary>
///     Handles NPC which become aggressive after being attacked.
/// </summary>
public sealed partial class NPCRetaliationSystem : EntitySystem // Frontier
{
    [Dependency] private readonly NpcFactionSystem _npcFaction = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    /// <inheritdoc />
    public override void Initialize()
    {
        SubscribeLocalEvent<NPCRetaliationComponent, DamageChangedEvent>(OnDamageChanged);
        SubscribeLocalEvent<NPCRetaliationComponent, DisarmedEvent>(OnDisarmed);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(_ => _investigationRoots.Clear()); // Frontier
    }

    private void OnDamageChanged(Entity<NPCRetaliationComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased)
            return;

        if (args.Origin is not {} origin)
            return;

        TryRetaliate(ent, origin, args.OriginCoordinates); // Frontier
    }

    private void OnDisarmed(Entity<NPCRetaliationComponent> ent, ref DisarmedEvent args)
    {
        TryRetaliate(ent, args.Source);
    }

    public bool TryRetaliate(Entity<NPCRetaliationComponent> ent, EntityUid target,
        EntityCoordinates? originCoordinates = null) // Frontier
    {
        // don't retaliate against inanimate objects.
        if (target == ent.Owner || TerminatingOrDeleted(target) || !HasComp<MobStateComponent>(target)) // Frontier
            return false;

        // don't retaliate against the same faction
        if (_npcFaction.IsEntityFriendly(ent.Owner, target))
            return false;

        // Frontier: only expire exceptions added by retaliation itself.
        if (ent.Comp.AttackMemories.ContainsKey(target) || !HasExplicitHostility(ent.Owner, target))
        {
            _npcFaction.AggroEntity(ent.Owner, target);
            if (ent.Comp.AttackMemoryLength is { } memoryLength)
                ent.Comp.AttackMemories[target] = _timing.CurTime + memoryLength;
        }

        RememberAttack(ent, target, originCoordinates);
        // End Frontier

        return true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        PruneMemories(); // Frontier: remove expired records on a shared interval.
    }
}
