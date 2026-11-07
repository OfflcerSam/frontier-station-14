using Robust.Shared.Map;
using Robust.Shared.Utility;

namespace Content.Server.NPC.Pathfinding;

public sealed partial class PathfindingSystem
{
    // Frontier: a slice limits one turn's work. The request owns its total budget and graph state.
    internal PathResult UpdateAStarPath(AStarPathRequest request, int? sliceLimit = null)
    {
        if (request.Start.Equals(request.End))
            return PathResult.Path;

        DebugTools.Assert(!request.Task.IsCompleted);
        request.Stopwatch.Restart();

        if (request.StartNode == null)
        {
            var start = GetPoly(request.Start);
            var end = GetPoly(request.End);
            if (start == null || end == null)
                return PathResult.NoPath;

            InitializeSearch(request, start);
            request.EndNode = end;
            request.EndPoint = _transform.WithEntityId(request.End, end.GraphUid);
        }

        var endNode = request.EndNode!;
        if (!request.StartNode!.IsValid() || !endNode.IsValid())
            return PathResult.NoPath;

        var sliceStart = request.Expansions;
        while (request.Frontier.Count > 0 && request.Expansions < NodeLimit)
        {
            if (ShouldYield(request, sliceStart, sliceLimit))
                return PathResult.Continuing;

            // Count actual work, including duplicate priority entries.
            request.Expansions++;
            var (_, current) = request.Frontier.Take();
            if (!current.IsValid())
                return PathResult.NoPath;

            if (current.Equals(endNode) ||
                request.Distance > 0f &&
                current.Coordinates.TryDistance(EntityManager, _transform, request.EndPoint, out var distance) &&
                distance <= request.Distance)
            {
                return CompleteSearch(request, current);
            }

            var currentCost = request.CostSoFar[current];
            foreach (var neighbor in current.Neighbors)
            {
                var tileCost = GetTileCost(request, current, neighbor);
                if (tileCost == 0f)
                    continue;

                var cost = currentCost + tileCost;
                if (request.CostSoFar.TryGetValue(neighbor, out var previous) && cost >= previous)
                    continue;

                request.CameFrom[neighbor] = current;
                request.CostSoFar[neighbor] = cost;
                // Bias ties toward the goal.
                var heuristic = OctileDistance(endNode, neighbor) * (1.0f + 1.0f / 1000.0f);
                request.Frontier.Add((cost + heuristic, neighbor));
            }
        }

        return request.Frontier.Count == 0 ? PathResult.NoPath : PathResult.BudgetExceeded;
    }
    // End Frontier
}
