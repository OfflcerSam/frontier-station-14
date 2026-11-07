using System.Threading;
using Content.Server.NPC.Components;

namespace Content.Server.NPC.HTN;

[RegisterComponent, AutoGenerateComponentPause] // Frontier: exclude paused time from scheduled elapsed time.
public sealed partial class HTNComponent : NPCComponent
{
    /// <summary>
    /// The base task to use for planning
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite),
    DataField("rootTask", required: true)]
    public HTNCompoundTask RootTask = default!;

    /// <summary>
    /// Whether the current task's utility services run on their configured intervals. // Frontier
    /// </summary>
    [DataField("checkServices")]
    public bool CheckServices = true;

    /// <summary>
    /// The NPC's current plan.
    /// </summary>
    [ViewVariables]
    public HTNPlan? Plan;

    /// <summary>
    /// How long to wait after having planned to try planning again.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField("planCooldown")]
    public float PlanCooldown = 0.45f;

    /// <summary>
    /// How much longer until we can try re-planning. This will happen even during update in case something changed.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public float PlanAccumulator = 0f;

    [DataField]
    public bool ConstantlyReplan = true;

    [ViewVariables]
    public HTNPlanJob? PlanningJob = null;

    [ViewVariables]
    public CancellationTokenSource? PlanningToken = null;

    /// <summary>
    /// Is this NPC currently planning?
    /// </summary>
    [ViewVariables] public bool Planning => PlanningJob != null;

    /// <summary>
    /// Determines whether plans should be made / updated for this entity
    /// </summary>
    [DataField]
    public bool Enabled = true;

    // Frontier: awake actors own one entry in the stable scheduler rotation.
    internal LinkedListNode<Entity<HTNComponent>>? ScheduleNode;
    internal long SchedulerTurn;

    [AutoPausedField]
    internal TimeSpan? LastScheduledAt;

    internal List<float>? ServiceCooldowns;
    internal bool ReplanRequested;
    internal uint PlanEnqueuedTick;
    // End Frontier
}
