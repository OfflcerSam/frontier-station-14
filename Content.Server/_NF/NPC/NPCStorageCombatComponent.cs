using System.Numerics;
using Robust.Shared.Map;

namespace Content.Server.NPC.Components;

/// <summary>
/// The container is a separate, bounded obstacle to reaching the combat target.
/// </summary>
[RegisterComponent]
internal sealed partial class NPCStorageCombatComponent : Component
{
    public EntityUid Hostile;
    public EntityUid Storage;
    public TimeSpan Deadline;
    public bool Welded;
    public bool Locked;
    public bool TriedOpen;
    public bool Failed;
    public EntityCoordinates? Goal;
    public bool HoldingPosition;
    public PursuitSettings? Pursuit;
    public Vector2? ApproachDirection;
    public int ApproachIndex;
    public bool UseMelee;
    public float LastDamage;
    public int ShotsWithoutProgress;
    public TimeSpan? DamageDeadline;

    public readonly record struct PursuitSettings(
        EntityCoordinates Goal, float Range, bool ArriveOnLineOfSight, float? InRangeMaxSpeed, bool DirectMove);
}
