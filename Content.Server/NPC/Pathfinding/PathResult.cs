namespace Content.Server.NPC.Pathfinding;

public enum PathResult : byte
{
    NoPath,
    PartialPath,
    Path,
    Continuing,
    BudgetExceeded, // Frontier: the search stopped with unexplored work.
}
