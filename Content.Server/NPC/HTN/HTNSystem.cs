using System.Linq;
using System.Text;
using System.Threading;
using Content.Server.Administration.Managers;
using Robust.Shared.CPUJob.JobQueues.Queues;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Content.Server.NPC.Systems;
using Content.Shared.Administration;
using Content.Shared.Mobs;
using Content.Shared.NPC;
using JetBrains.Annotations;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using Content.Server.Worldgen; // Frontier
using Content.Server.Worldgen.Components; // Frontier
using Content.Server.Worldgen.Systems; // Frontier
using Robust.Server.GameObjects; // Frontier

namespace Content.Server.NPC.HTN;

public sealed partial class HTNSystem : EntitySystem // Frontier
{
    [Dependency] private readonly IAdminManager _admin = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly NPCUtilitySystem _utility = default!;
    // Frontier
    [Dependency] private readonly WorldControllerSystem _world = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    private EntityQuery<WorldControllerComponent> _mapQuery;
    // Frontier

    private readonly JobQueue _planQueue = new(0.004);

    private readonly HashSet<ICommonSession> _subscribers = new();

    // Hierarchical Task Network
    public override void Initialize()
    {
        base.Initialize();
        _mapQuery = GetEntityQuery<WorldControllerComponent>(); // Frontier
        InitializeScheduler(); // Frontier
        SubscribeLocalEvent<HTNComponent, MobStateChangedEvent>(_npc.OnMobStateChange);
        SubscribeLocalEvent<HTNComponent, MapInitEvent>(_npc.OnNPCMapInit);
        SubscribeLocalEvent<HTNComponent, PlayerAttachedEvent>(_npc.OnPlayerNPCAttach);
        SubscribeLocalEvent<HTNComponent, PlayerDetachedEvent>(_npc.OnPlayerNPCDetach);
        SubscribeLocalEvent<HTNComponent, ComponentShutdown>(OnHTNShutdown);
        SubscribeNetworkEvent<RequestHTNMessage>(OnHTNMessage);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypeLoad);
        OnLoad();
    }

    private void OnHTNMessage(RequestHTNMessage msg, EntitySessionEventArgs args)
    {
        if (!_admin.HasAdminFlag(args.SenderSession, AdminFlags.Debug))
        {
            _subscribers.Remove(args.SenderSession);
            return;
        }

        if (_subscribers.Add(args.SenderSession))
            return;

        _subscribers.Remove(args.SenderSession);
    }

    private void OnLoad()
    {
        // Clear all NPCs in case they're hanging onto stale tasks
        var query = AllEntityQuery<HTNComponent>();

        while (query.MoveNext(out var comp))
        {
            // Frontier: pending jobs also hold references to old prototypes.
            StopNPC(comp);
            Replan(comp);
            // End Frontier
        }

        // Add dependencies for all operators.
        // We put code on operators as I couldn't think of a clean way to put it on systems.
        foreach (var compound in _prototypeManager.EnumeratePrototypes<HTNCompoundPrototype>())
        {
            UpdateCompound(compound);
        }
    }

    private void OnPrototypeLoad(PrototypesReloadedEventArgs obj)
    {
        OnLoad();
    }

    private void UpdateCompound(HTNCompoundPrototype compound)
    {
        for (var i = 0; i < compound.Branches.Count; i++)
        {
            var branch = compound.Branches[i];

            foreach (var precon in branch.Preconditions)
            {
                precon.Initialize(EntityManager.EntitySysManager);
            }

            foreach (var task in branch.Tasks)
            {
                UpdateTask(task);
            }
        }
    }

    private void UpdateTask(HTNTask task)
    {
        switch (task)
        {
            case HTNCompoundTask:
                // NOOP, handled elsewhere
                break;
            case HTNPrimitiveTask primitive:
                foreach (var precon in primitive.Preconditions)
                {
                    precon.Initialize(EntityManager.EntitySysManager);
                }

                primitive.Operator.Initialize(EntityManager.EntitySysManager);
                break;
            default:
                throw new NotImplementedException();
        }
    }

    private void OnHTNShutdown(EntityUid uid, HTNComponent component, ComponentShutdown args)
    {
        Unschedule(component); // Frontier
        _npc.OnNPCShutdown(uid, component, args);
    }

    /// <summary>
    /// Enable / disable the hierarchical task network of an entity
    /// </summary>
    /// <param name="ent">The entity and its <see cref="HTNComponent"/></param>
    /// <param name="state">Set 'true' to enable, or 'false' to disable, the HTN</param>
    /// <param name="planCooldown">Specifies a time in seconds before the entity can start planning a new action (only takes effect when the HTN is enabled)</param>
    // ReSharper disable once InconsistentNaming
    [PublicAPI]
    public void SetHTNEnabled(Entity<HTNComponent> ent, bool state, float planCooldown = 0f)
    {
        if (ent.Comp.Enabled == state)
            return;

        ent.Comp.Enabled = state;
        ent.Comp.PlanAccumulator = planCooldown;

        StopNPC(ent.Comp); // Frontier: retire any result from before the change.

        if (ent.Comp.Enabled && ent.Comp.PlanAccumulator <= 0)
            RequestPlan(ent.Comp);
    }

    /// <summary>
    /// Forces the NPC to replan.
    /// </summary>
    [PublicAPI]
    public void Replan(HTNComponent component)
    {
        CancelPlanning(component); // Frontier: an explicit replan invalidates the previous snapshot.
        component.PlanAccumulator = 0f;
        component.ReplanRequested = true; // Frontier: explicitly requested replans also apply to nonconstant plans.
    }

    // Frontier: skip handling entities on unloaded chunks
    private bool IsNPCActive(EntityUid entity)
    {
        var transform = Transform(entity);

        if (!_mapQuery.TryGetComponent(transform.MapUid, out var worldComponent))
            return true;

        var coordinates = WorldGen.WorldToChunkCoords(_transform.GetWorldPosition(transform)).Floored();

        return _world.IsChunkLoaded(worldComponent, coordinates);
    }
    // End Frontier: skip handling entities on unloaded chunks

    private void AppendDebugText(HTNTask task, StringBuilder text, List<int> planBtr, List<int> btr, ref int level)
    {
        // If it's the selected BTR then highlight.
        for (var i = 0; i < btr.Count; i++)
        {
            text.Append("--");
        }

        text.Append(' ');

        if (task is HTNPrimitiveTask primitive)
        {
            text.AppendLine(primitive.ToString());
            return;
        }

        if (task is HTNCompoundTask compTask)
        {
            var compound = _prototypeManager.Index<HTNCompoundPrototype>(compTask.Task);
            level++;
            text.AppendLine(compound.ID);
            var branches = compound.Branches;

            for (var i = 0; i < branches.Count; i++)
            {
                var branch = branches[i];
                btr.Add(i);
                text.AppendLine($" branch {string.Join(", ", btr)}:");

                foreach (var sub in branch.Tasks)
                {
                    AppendDebugText(sub, text, planBtr, btr, ref level);
                }

                btr.RemoveAt(btr.Count - 1);
            }

            level--;
            return;
        }

        throw new NotImplementedException();
    }

    // Frontier: the planner and running task have separate elapsed intervals.
    private void Update(HTNComponent component, float elapsed, float taskElapsed, bool wasPlanning)
    {
        // If we're not planning then countdown to next one.
        if (!wasPlanning)
            component.PlanAccumulator -= elapsed;

        // We'll still try re-planning occasionally even when we're updating in case new data comes in.
        if ((component.ConstantlyReplan || component.Plan is null || component.ReplanRequested) &&
            component.PlanAccumulator <= 0f)
        {
            RequestPlan(component);
        }

        // Getting a new plan so do nothing.
        if (component.Plan == null)
            return;

        // Run the existing plan still
        var status = HTNOperatorStatus.Finished;

        // Continuously run operators until we can't anymore.
        while (status != HTNOperatorStatus.Continuing && component.Plan is { } plan)
        {
            // Run the existing operator
            var currentOperator = plan.CurrentOperator;
            var blackboard = component.Blackboard;

            UpdateServices(component, plan.CurrentTask, taskElapsed);
            if (component.Plan != plan)
                return;

            status = currentOperator.Update(blackboard, taskElapsed);
            if (component.Plan != plan)
                return;

            switch (status)
            {
                case HTNOperatorStatus.Continuing:
                    break;
                case HTNOperatorStatus.Failed:
                    CancelPlanning(component);
                    ShutdownTask(currentOperator, blackboard, status);
                    if (component.Plan == plan)
                        ShutdownPlan(component);
                    break;
                // Operator completed so go to the next one.
                case HTNOperatorStatus.Finished:
                    CancelPlanning(component);
                    ShutdownTask(currentOperator, blackboard, status);
                    if (component.Plan != plan)
                        return;

                    plan.Index++;

                    // Plan finished!
                    if (plan.Tasks.Count <= plan.Index)
                    {
                        ShutdownPlan(component);
                        break;
                    }

                    StartupTask(component, plan.CurrentTask, plan.Effects[plan.Index]);
                    // Only the task that was running between turns can consume that time.
                    taskElapsed = 0f;
                    break;
                default:
                    throw new InvalidOperationException();
            }
        }
    }
    // End Frontier

    public void ShutdownTask(HTNOperator currentOperator, NPCBlackboard blackboard, HTNOperatorStatus status)
    {
        if (currentOperator is IHtnConditionalShutdown conditional &&
            (conditional.ShutdownState & HTNPlanState.TaskFinished) != 0x0)
        {
            conditional.ConditionalShutdown(blackboard);
        }

        currentOperator.TaskShutdown(blackboard, status);
    }

    public void ShutdownPlan(HTNComponent component)
    {
        DebugTools.Assert(component.Plan != null);
        // Frontier: results planned against the ended task must never be installed.
        var plan = component.Plan;
        component.Plan = null;
        component.ServiceCooldowns?.Clear();
        CancelPlanning(component);
        if (plan == null)
            return;
        // End Frontier
        var blackboard = component.Blackboard;

        foreach (var task in plan.Tasks) // Frontier
        {
            if (task.Operator is IHtnConditionalShutdown conditional &&
                (conditional.ShutdownState & HTNPlanState.PlanFinished) != 0x0)
            {
                conditional.ConditionalShutdown(blackboard);
            }

            task.Operator.PlanShutdown(component.Blackboard);
        }
    }

    /// <summary>
    /// Starts a new primitive task. Will apply effects from planning if applicable.
    /// </summary>
    private void StartupTask(HTNComponent component, HTNPrimitiveTask primitive, Dictionary<string, object>? effects) // Frontier
    {
        // Frontier: a new task gets its own utility service intervals.
        component.ServiceCooldowns?.Clear();
        var blackboard = component.Blackboard;
        // End Frontier
        // We may have planner only tasks where we want to reuse their data during update
        // e.g. if we pathfind to an enemy to know if we can attack it, we don't want to do another pathfind immediately
        if (effects != null && primitive.ApplyEffectsOnStartup)
        {
            foreach (var (key, value) in effects)
            {
                blackboard.SetValue(key, value);
            }
        }

        primitive.Operator.Startup(blackboard);
    }

    /// <summary>
    /// Request a new plan for this component, even if running an existing plan.
    /// </summary>
    /// <param name="component"></param>
    private void RequestPlan(HTNComponent component)
    {
        if (component.PlanningJob != null)
            return;

        component.PlanAccumulator = component.PlanCooldown;
        var cancelToken = new CancellationTokenSource();
        var branchTraversal = component.Plan?.BranchTraversalRecord;

        var job = new HTNPlanJob(
            0.02,
            _prototypeManager,
            component.RootTask,
            component.Blackboard.ShallowClone(), branchTraversal, cancelToken.Token);

        _planQueue.EnqueueJob(job);
        component.PlanningJob = job;
        component.PlanningToken = cancelToken;
    }

    public string GetDomain(HTNCompoundTask compound)
    {
        // TODO: Recursively add each one
        var indent = 0;
        var builder = new StringBuilder();
        AppendDomain(builder, compound, ref indent);

        return builder.ToString();
    }

    private void AppendDomain(StringBuilder builder, HTNTask task, ref int indent)
    {
        var buffer = string.Concat(Enumerable.Repeat("    ", indent));

        if (task is HTNPrimitiveTask primitive)
        {
            builder.AppendLine(buffer + $"Primitive: {task}");
            builder.AppendLine(buffer + $"  operator: {primitive.Operator.GetType().Name}");
        }
        else if (task is HTNCompoundTask compTask)
        {
            var compound = _prototypeManager.Index<HTNCompoundPrototype>(compTask.Task);
            builder.AppendLine(buffer + $"Compound: {task}");

            for (var i = 0; i < compound.Branches.Count; i++)
            {
                var branch = compound.Branches[i];

                builder.AppendLine(buffer + "  branch:");
                indent++;

                foreach (var branchTask in branch.Tasks)
                {
                    AppendDomain(builder, branchTask, ref indent);
                }

                indent--;
            }
        }
    }
}

/// <summary>
/// The outcome of the current operator during update.
/// </summary>
public enum HTNOperatorStatus : byte
{
    Continuing,
    Failed,
    Finished,

    /// <summary>
    /// Was a better plan than this found?
    /// </summary>
    BetterPlan,
}
