using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Components;
using Content.Server.NPC.Systems; // Frontier
using Content.Shared.CombatMode;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Shared.Audio;
using Robust.Shared.Map; // Frontier

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;

public sealed partial class GunOperator : HTNOperator, IHtnConditionalShutdown
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField("shutdownState")]
    public HTNPlanState ShutdownState { get; private set; } = HTNPlanState.TaskFinished;

    /// <summary>
    /// Key that contains the target entity.
    /// </summary>
    [DataField("targetKey", required: true)]
    public string TargetKey = default!;

    // Frontier: only gun plans with a background MoveTo opt into steering ownership.
    [DataField]
    public string? PursuitTargetKey;
    // End Frontier

    /// <summary>
    /// Minimum damage state that the target has to be in for us to consider attacking.
    /// </summary>
    [DataField("targetState")]
    public MobState TargetState = MobState.Alive;

    /// <summary>
    /// Do we require line of sight of the target before failing.
    /// </summary>
    [DataField("requireLOS")]
    public bool RequireLOS = false;

    /// <summary>
    /// If true, only opaque objects will block line of sight.
    /// </summary>
    [DataField("opaqueKey")]
    public bool UseOpaqueForLOSChecks = false;

    // Like movement we add a component and pass it off to the dedicated system.

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        // Don't attack if they're already as wounded as we want them.
        if (!blackboard.TryGetValue<EntityUid>(TargetKey, out var target, _entManager))
        {
            return (false, null);
        }

        if (_entManager.TryGetComponent<MobStateComponent>(target, out var mobState) &&
            mobState.CurrentState > TargetState)
        {
            return (false, null);
        }

        return (true, null);
    }

    public override void Startup(NPCBlackboard blackboard)
    {
        base.Startup(blackboard);

        // Frontier: initialize the same target state used when the utility service switches targets.
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var ranged = _entManager.EnsureComponent<NPCRangedCombatComponent>(owner);
        SetTarget(owner, ranged, blackboard, blackboard.GetValue<EntityUid>(TargetKey));
        // End Frontier
        ranged.UseOpaqueForLOSChecks = UseOpaqueForLOSChecks;

        if (blackboard.TryGetValue<float>(NPCBlackboard.RotateSpeed, out var rotSpeed, _entManager))
        {
            ranged.RotationSpeed = new Angle(rotSpeed);
        }

        if (blackboard.TryGetValue<SoundSpecifier>("SoundTargetInLOS", out var losSound, _entManager))
        {
            ranged.SoundTargetInLOS = losSound;
        }
    }

    public void ConditionalShutdown(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        _entManager.System<SharedCombatModeSystem>().SetInCombatMode(owner, false);
        _entManager.RemoveComponent<NPCRangedCombatComponent>(owner);
        blackboard.Remove<EntityUid>(TargetKey);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        base.Update(blackboard, frameTime);
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        HTNOperatorStatus status;

        if (_entManager.TryGetComponent<NPCRangedCombatComponent>(owner, out var combat) &&
            blackboard.TryGetValue<EntityUid>(TargetKey, out var target, _entManager))
        {
            if (combat.Target != target) // Frontier: preserve aim and steering when the same entity moves.
                SetTarget(owner, combat, blackboard, target); // Frontier

            // Success
            if (_entManager.TryGetComponent<MobStateComponent>(combat.Target, out var mobState) &&
                mobState.CurrentState > TargetState)
            {
                status = HTNOperatorStatus.Finished;
            }
            else
            {
                switch (combat.Status)
                {
                    case CombatStatus.TargetUnreachable:
                        status = HTNOperatorStatus.Failed;
                        break;
                    case CombatStatus.NotInSight:
                        if (RequireLOS)
                            status = HTNOperatorStatus.Failed;
                        else
                            status = HTNOperatorStatus.Continuing;
                        break;
                    case CombatStatus.Normal:
                        status = HTNOperatorStatus.Continuing;
                        break;
                    default:
                        status = HTNOperatorStatus.Failed;
                        break;
                }
            }
        }
        else
        {
            status = HTNOperatorStatus.Failed;
        }

        // Mark it as finished to continue the plan.
        if (status == HTNOperatorStatus.Continuing && ShutdownState == HTNPlanState.PlanFinished)
        {
            status = HTNOperatorStatus.Finished;
        }

        return status;
    }

    // Frontier: keep the coordinate key in sync with the active pursuit and its target.
    private void SetTarget(EntityUid owner, NPCRangedCombatComponent combat, NPCBlackboard blackboard, EntityUid target)
    {
        EntityCoordinates? previous = null;
        if (PursuitTargetKey is { } key && blackboard.TryGetValue<EntityCoordinates>(key, out var coordinates, _entManager))
            previous = coordinates;

        var goal = _entManager.System<NPCCombatSystem>().SetRangedTarget((owner, combat), target, previous);
        if (PursuitTargetKey is not { } pursuitKey || previous == null)
            return;

        if (goal is { } destination)
            blackboard.SetValue(pursuitKey, destination);
        else
            blackboard.Remove<EntityCoordinates>(pursuitKey);
    }
    // End Frontier
}
