using System.Text;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Content.Server.NPC.Queries;
using Content.Shared.NPC;
using Robust.Shared.CPUJob.JobQueues;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.NPC.HTN;

public sealed partial class HTNSystem
{
    [Dependency] private readonly IGameTiming _schedulerTiming = default!;
    [Dependency] private readonly IRobustRandom _serviceRandom = default!;

    // Rotate existing nodes in place so skipping an actor never changes where the next tick resumes.
    private readonly LinkedList<Entity<HTNComponent>> _schedule = new();
    private long _schedulerTurn;

    private void InitializeScheduler()
    {
        SubscribeLocalEvent<HTNComponent, ComponentStartup>(OnSchedulerHTNStartup);
        // Frontier's protected-grid check already owns ActiveNPC's startup subscription.
        SubscribeLocalEvent<ActiveNPCComponent, ComponentInit>(OnSchedulerWake);
        SubscribeLocalEvent<ActiveNPCComponent, ComponentShutdown>(OnSchedulerSleep);
    }

    private void OnSchedulerHTNStartup(EntityUid uid, HTNComponent component, ComponentStartup args)
    {
        if (HasComp<ActiveNPCComponent>(uid))
            Schedule((uid, component));
    }

    private void OnSchedulerWake(EntityUid uid, ActiveNPCComponent component, ComponentInit args)
    {
        if (TryComp<HTNComponent>(uid, out var htn))
            Schedule((uid, htn));
    }

    private void OnSchedulerSleep(EntityUid uid, ActiveNPCComponent component, ComponentShutdown args)
    {
        if (!TryComp<HTNComponent>(uid, out var htn))
            return;

        Unschedule(htn);
        StopNPC(htn);
    }

    private void Schedule(Entity<HTNComponent> npc)
    {
        if (npc.Comp.ScheduleNode == null && !TerminatingOrDeleted(npc.Owner))
            npc.Comp.ScheduleNode = _schedule.AddLast(npc);
    }

    private void Unschedule(HTNComponent component)
    {
        if (component.ScheduleNode is not { } node)
            return;

        _schedule.Remove(node);
        component.ScheduleNode = null;
    }

    private static void CancelPlanning(HTNComponent component)
    {
        // Detach first so even a synchronous cancellation callback cannot publish an old result.
        var token = component.PlanningToken;
        component.PlanningToken = null;
        component.PlanningJob = null;
        // A new snapshot must still fulfill an explicit request after this one is invalidated.
        if (token != null && component.ReplanRequested)
            component.PlanAccumulator = 0f;
        token?.Cancel();
    }

    internal void StopNPC(HTNComponent component)
    {
        component.LastScheduledAt = null;
        component.ReplanRequested = false;
        CancelPlanning(component);
        if (component.Plan is not { } plan)
            return;

        ShutdownTask(plan.CurrentOperator, component.Blackboard, HTNOperatorStatus.Failed);
        if (component.Plan == plan)
            ShutdownPlan(component);
    }

    internal void SuspendScheduler()
    {
        foreach (var (_, component) in _schedule)
        {
            component.LastScheduledAt = null;
            CancelPlanning(component);
        }
    }

    public void UpdateNPC(int maxUpdates, float frameTime)
    {
        _planQueue.Process();
        var now = _schedulerTiming.CurTime;
        var turn = ++_schedulerTurn;
        var remaining = _schedule.Count;
        var updates = 0;
        while (remaining-- > 0 && updates < maxUpdates && _schedule.First is { } node)
        {
            var (uid, component) = node.Value;
            // Callbacks can add or remove actors. Never process the same entry twice this turn.
            if (component.SchedulerTurn == turn)
                break;

            component.SchedulerTurn = turn;
            _schedule.RemoveFirst();
            _schedule.AddLast(node);

            if (!component.Enabled || Paused(uid) || !IsNPCActive(uid))
            {
                component.LastScheduledAt = null;
                CancelPlanning(component);
                continue;
            }

            var pending = component.PlanningJob;
            if (component.Plan == null && pending is { Status: not JobStatus.Finished })
            {
                // There is no task to advance yet, so another actor can use this slot.
                component.LastScheduledAt = null;
                continue;
            }

            var elapsed = component.LastScheduledAt is { } last
                ? (float) Math.Max(0, (now - last).TotalSeconds)
                : frameTime;
            component.LastScheduledAt = now;
            updates++;

            var started = ApplyPlanningResult(uid, component);
            if (component.ScheduleNode == node && component.Enabled)
                Update(component, elapsed, started ? 0f : elapsed, pending != null);
        }
    }

    private bool ApplyPlanningResult(EntityUid uid, HTNComponent component)
    {
        if (component.PlanningJob is not { Status: JobStatus.Finished } job)
            return false;

        component.PlanningJob = null;
        component.PlanningToken?.Dispose();
        component.PlanningToken = null;
        component.ReplanRequested = false;

        if (job.Exception is { } exception)
        {
            Log.Fatal($"Received exception on planning job for {uid}!");
            _npc.SleepNPC(uid, component);
            RemComp<HTNComponent>(uid);
            throw exception;
        }

        var replacement = job.Result;
        if (replacement == null)
        {
            if (component.Plan == null)
                ReportPlan(uid, component, null);
            return false;
        }

        if (component.Plan is { } current)
        {
            if (!IsBetterPlan(current, replacement))
                return false;

            ShutdownTask(current.CurrentOperator, component.Blackboard, HTNOperatorStatus.BetterPlan);
            if (component.Plan != current)
                return false;

            ShutdownPlan(component);
            if (component.ScheduleNode == null || !component.Enabled)
                return false;
        }

        component.Plan = replacement;
        StartupTask(component, replacement.CurrentTask, replacement.Effects[replacement.Index]);
        ReportPlan(uid, component, replacement);
        return true;
    }

    private static bool IsBetterPlan(HTNPlan current, HTNPlan replacement)
    {
        var oldBranches = current.BranchTraversalRecord;
        var newBranches = replacement.BranchTraversalRecord;
        for (var i = 0; i < Math.Min(oldBranches.Count, newBranches.Count); i++)
        {
            // A deeper choice cannot override the priority of an earlier branch.
            if (oldBranches[i] != newBranches[i])
                return newBranches[i] < oldBranches[i];
        }

        // Keep the running plan when the shared traversal establishes no higher priority.
        return false;
    }

    private void ReportPlan(EntityUid uid, HTNComponent component, HTNPlan? plan)
    {
        if (_subscribers.Count == 0)
            return;

        var message = string.Empty;
        if (plan != null)
        {
            var text = new StringBuilder();
            text.AppendLine($"BTR: {string.Join(", ", plan.BranchTraversalRecord)}");
            text.AppendLine("tasks:");
            var level = -1;
            AppendDebugText(component.RootTask, text, plan.BranchTraversalRecord, new List<int>(), ref level);
            message = text.ToString();
        }
        var netUid = GetNetEntity(uid);
        foreach (var session in _subscribers)
            RaiseNetworkEvent(new HTNMessage { Uid = netUid, Text = message }, session.Channel);
    }

    private void UpdateServices(HTNComponent component, HTNPrimitiveTask task, float elapsed)
    {
        if (!component.CheckServices || task.Services.Count == 0)
            return;

        var cooldowns = component.ServiceCooldowns ??= new List<float>(task.Services.Count);
        if (cooldowns.Count != task.Services.Count)
        {
            cooldowns.Clear();
            foreach (var service in task.Services)
                cooldowns.Add(GetServiceCooldown(service));
        }

        var blackboard = component.Blackboard;
        for (var i = 0; i < task.Services.Count; i++)
        {
            cooldowns[i] -= elapsed;
            if (cooldowns[i] > 0f)
                continue;

            var service = task.Services[i];
            var target = _utility.GetEntities(blackboard, service.Prototype).GetHighest();
            if (!blackboard.TryGetValue<EntityUid>(service.Key, out var current, EntityManager) || current != target)
            {
                blackboard.SetValue(service.Key, target);
                // A replacement planned against the previous target is no longer current.
                CancelPlanning(component);
            }

            // A late turn runs the service once rather than replaying missed polls.
            cooldowns[i] = GetServiceCooldown(service);
        }
    }

    private float GetServiceCooldown(UtilityService service)
    {
        var min = Math.Max(0f, service.MinCooldown);
        return service.MaxCooldown > min ? _serviceRandom.NextFloat(min, service.MaxCooldown) : min;
    }
}
