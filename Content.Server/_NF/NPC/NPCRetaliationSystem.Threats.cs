using System.Linq;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Content.Server.NPC.HTN.PrimitiveTasks.Operators;
using Content.Shared.Examine;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC;
using Content.Shared.NPC.Components;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server.NPC.Systems;

public sealed partial class NPCRetaliationSystem
{
    private static readonly TimeSpan ThreatLifetime = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromSeconds(0.2);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(0.5);

    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly HTNSystem _htn = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private TimeSpan _nextCleanup;
    private readonly Dictionary<string, bool> _investigationRoots = new();

    private bool HasExplicitHostility(EntityUid owner, EntityUid target) =>
        _npcFaction.GetHostiles(owner).Contains(target);

    private bool IsThreatSource(Entity<NPCRetaliationComponent> ent, EntityUid target, out TimeSpan? hostilityExpiry)
    {
        hostilityExpiry = null;
        if (TerminatingOrDeleted(target) || _npcFaction.IsEntityFriendly(ent.Owner, target) ||
            _npcFaction.IsIgnored(ent.Owner, target))
        {
            return false;
        }

        if (TryComp<NpcFactionMemberComponent>(ent, out var ownerFaction) &&
            TryComp<NpcFactionMemberComponent>(target, out var targetFaction))
        {
            var nativeHostiles = ownerFaction.HostileFactions;
            if (nativeHostiles.Overlaps(targetFaction.Factions))
                return true;
        }

        if (!HasExplicitHostility(ent.Owner, target))
            return false;

        if (!ent.Comp.AttackMemories.TryGetValue(target, out var expires))
            return true;

        hostilityExpiry = expires;
        return expires > _timing.CurTime;
    }

    internal bool RequiresObservedTarget(EntityUid owner) =>
        TryComp<NPCRetaliationComponent>(owner, out var retaliation) && retaliation.InvestigateThreats &&
        TryComp<HTNComponent>(owner, out var htn) && RootSupportsInvestigation(htn.RootTask.Task);

    private bool RootSupportsInvestigation(string id)
    {
        // Descendants may replace the inherited root with a follower or other custom plan.
        if (_investigationRoots.TryGetValue(id, out var supported))
            return supported;

        supported = _prototypes.TryIndex(new ProtoId<HTNCompoundPrototype>(id), out var root) &&
            root.Branches.Any(branch => branch.Tasks.Any(task => task is HTNCompoundTask
            {
                Task: "NFInvestigateThreatCompound"
            } or HTNPrimitiveTask { Operator: InvestigateThreatOperator }));
        _investigationRoots[id] = supported;
        return supported;
    }

    private bool CanInvestigate(EntityUid owner) =>
        !TerminatingOrDeleted(owner) && !HasComp<ActorComponent>(owner) &&
        HasComp<ActiveNPCComponent>(owner) && !_mobState.IsIncapacitated(owner) &&
        TryComp<HTNComponent>(owner, out var htn) && htn.Enabled;

    private void RememberAttack(Entity<NPCRetaliationComponent> ent, EntityUid target, EntityCoordinates? origin)
    {
        if (!RequiresObservedTarget(ent) || !CanInvestigate(ent) || _npcFaction.IsIgnored(ent.Owner, target))
            return;

        var location = origin ?? _transform.GetMoverCoordinates(target);
        RememberThreat(ent, target, location, _timing.CurTime);
    }

    private void RememberThreat(Entity<NPCRetaliationComponent> ent, EntityUid target,
        EntityCoordinates location, TimeSpan recordedAt)
    {
        // A newly lost sighting must not displace a more recent stimulus.
        var previous = ent.Comp.Threat;
        if (previous?.RecordedAt > recordedAt)
            return;

        if (!IsThreatSource(ent, target, out var hostilityExpiry) || !location.IsValid(EntityManager) ||
            _transform.ToMapCoordinates(location).MapId != Transform(ent).MapID)
            return;

        var expires = recordedAt + ThreatLifetime;
        // Temporary hostility must outlive the investigation, even if an old combat plan still exists.
        if (hostilityExpiry is { } until && until < expires)
            expires = until;
        if (expires <= _timing.CurTime)
            return;

        ent.Comp.Threat = new NPCThreatStimulus(target, location, recordedAt, expires);
        if ((previous == null || previous.Value.Source != target || previous.Value.ExpiresAt <= _timing.CurTime) &&
            TryComp<HTNComponent>(ent, out var htn))
        {
            _htn.Replan(htn);
        }
    }

    internal bool TryGetThreat(EntityUid owner, out NPCThreatStimulus stimulus)
    {
        if (TryComp<NPCRetaliationComponent>(owner, out var retaliation) &&
            retaliation.Threat is { } threat && threat.ExpiresAt > _timing.CurTime &&
            CanInvestigate(owner) && RequiresObservedTarget(owner) &&
            IsThreatSource((owner, retaliation), threat.Source, out _) &&
            threat.Location.IsValid(EntityManager) && _transform.ToMapCoordinates(threat.Location).MapId == Transform(owner).MapID)
        {
            stimulus = threat;
            return true;
        }

        stimulus = default;
        return false;
    }

    internal void ConsumeThreat(EntityUid owner, NPCThreatStimulus stimulus)
    {
        if (TryComp<NPCRetaliationComponent>(owner, out var retaliation) && retaliation.Threat == stimulus)
            retaliation.Threat = null;
    }

    internal bool CanPursueVisibleTarget(EntityUid owner, EntityUid target)
    {
        if (!RequiresObservedTarget(owner) || !HasComp<MobStateComponent>(target))
            return true;

        if (!CanInvestigate(owner))
            return false;

        var retaliation = Comp<NPCRetaliationComponent>(owner);
        var now = _timing.CurTime;
        var previous = retaliation.Observation;
        if (previous is { } observation && observation.Target == target && now - observation.SampledAt < ObservationInterval)
            return observation.Location != null;

        if (CanSeeTarget(owner, target))
        {
            retaliation.Observation = new NPCThreatObservation(target, _transform.GetMoverCoordinates(target), now);
            return true;
        }

        // Remember only the last verified location. An unseen target never updates the point.
        if (previous is { Location: { } location } seen && seen.Target == target)
            RememberThreat((owner, retaliation), target, location, seen.SampledAt);

        retaliation.Observation = new NPCThreatObservation(target, null, now);
        return false;
    }

    internal void RetireObservation(EntityUid owner, EntityUid target)
    {
        if (!TryComp<NPCRetaliationComponent>(owner, out var retaliation) ||
            retaliation.Observation is not { } seen || seen.Target != target)
        {
            return;
        }

        // HTN can replace the combat task before its next sight sample.
        retaliation.Observation = null;
        if (seen.Location is { } location && !TerminatingOrDeleted(target) && CanInvestigate(owner) &&
            RequiresObservedTarget(owner) && !CanSeeTarget(owner, target))
        {
            RememberThreat((owner, retaliation), target, location, seen.SampledAt);
        }
    }

    private bool CanSeeTarget(EntityUid owner, EntityUid target)
    {
        var board = Comp<HTNComponent>(owner).Blackboard;
        var range = board.GetValueOrDefault<float>(board.GetVisionRadiusKey(EntityManager), EntityManager) + 0.5f;
        return _examine.InRangeUnOccluded(owner, target, range);
    }

    private void PruneMemories()
    {
        var now = _timing.CurTime;
        if (now < _nextCleanup)
            return;
        _nextCleanup = now + CleanupInterval;

        var query = EntityQueryEnumerator<NPCRetaliationComponent>();
        while (query.MoveNext(out var uid, out var retaliation))
        {
            // Removing current entries during Dictionary enumeration is supported.
            foreach (var (source, expires) in retaliation.AttackMemories)
            {
                if (now < expires && !TerminatingOrDeleted(source))
                    continue;

                retaliation.AttackMemories.Remove(source);
                _npcFaction.DeAggroEntity(uid, source);
            }

            if (retaliation.Threat is { } threat && (now >= threat.ExpiresAt || TerminatingOrDeleted(threat.Source)))
                retaliation.Threat = null;
        }
    }
}
