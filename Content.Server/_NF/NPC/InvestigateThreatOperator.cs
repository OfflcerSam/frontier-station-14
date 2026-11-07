using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators;

/// <summary>
/// Move to a stored attack stimulus while the ordinary combat branches look for visible targets.
/// </summary>
public sealed partial class InvestigateThreatOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entities = default!;
    private NPCRetaliationSystem _retaliation = default!;
    private NPCSteeringSystem _steering = default!;
    private SharedTransformSystem _transform = default!;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _retaliation = sysManager.GetEntitySystem<NPCRetaliationSystem>();
        _steering = sysManager.GetEntitySystem<NPCSteeringSystem>();
        _transform = sysManager.GetEntitySystem<SharedTransformSystem>();
    }

    public override Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        return Task.FromResult<(bool, Dictionary<string, object>?)>((_retaliation.TryGetThreat(owner, out _), null));
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        // Read the current snapshot at execution time. A newer hit may have replaced the planned stimulus.
        if (!_retaliation.TryGetThreat(owner, out var threat))
            return HTNOperatorStatus.Finished;

        if (_entities.TryGetComponent<NPCSteeringComponent>(owner, out var current) && current.Investigation == threat)
        {
            if (current.Status == SteeringStatus.Moving)
                return HTNOperatorStatus.Continuing;

            _retaliation.ConsumeThreat(owner, threat);
            return current.Status == SteeringStatus.InRange ? HTNOperatorStatus.Finished : HTNOperatorStatus.Failed;
        }

        var steering = _steering.Register(owner, threat.Location);
        if (steering.Status != SteeringStatus.Moving)
            _steering.SetPlannedPath(owner, steering);
        steering.Investigation = threat;
        steering.Range = 1f;
        steering.ArriveOnLineOfSight = false;
        steering.InRangeMaxSpeed = null;
        var grid = _transform.GetGrid(owner);
        steering.DirectMove = grid == null || grid != _transform.GetGrid(threat.Location);
        return HTNOperatorStatus.Continuing;
    }

    public override void TaskShutdown(NPCBlackboard blackboard, HTNOperatorStatus status)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        if (_entities.TryGetComponent<NPCSteeringComponent>(owner, out var steering) && steering.Investigation != null)
            _steering.Unregister(owner, steering);
    }
}
