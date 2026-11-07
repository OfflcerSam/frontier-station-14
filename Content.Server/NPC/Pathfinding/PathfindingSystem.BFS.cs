using Robust.Shared.Map;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Server.NPC.Pathfinding;

public sealed partial class PathfindingSystem
{
    // Frontier: bound the sampled neighborhood across all slices, including discovered endpoints.
    internal PathResult UpdateBFSPath(IRobustRandom random, BFSPathRequest request, int? sliceLimit = null)
    {
        DebugTools.Assert(!request.Task.IsCompleted);
        request.Stopwatch.Restart();

        if (request.StartNode == null)
        {
            var start = GetPoly(request.Start);
            if (start == null)
                return PathResult.NoPath;

            InitializeSearch(request, start);
        }

        var startNode = request.StartNode!;
        if (!startNode.IsValid())
            return PathResult.NoPath;

        var range = Math.Max(0f, request.ExpansionRange);
        var sliceStart = request.Expansions;
        var expansionLimit = Math.Min(NodeLimit, request.ExpansionLimit);
        while (request.Frontier.Count > 0 && request.Expansions < expansionLimit)
        {
            if (ShouldYield(request, sliceStart, sliceLimit))
                return PathResult.Continuing;

            request.Expansions++;
            var (_, current) = request.Frontier.Take();
            if (!current.IsValid())
                return PathResult.NoPath;

            var currentCost = request.CostSoFar[current];
            foreach (var neighbor in current.Neighbors)
            {
                var tileCost = GetTileCost(request, current, neighbor);
                if (tileCost == 0f)
                    continue;

                var cost = currentCost + tileCost;
                if (request.CostSoFar.TryGetValue(neighbor, out var previous) && cost >= previous)
                    continue;

                if (!WithinWanderRadius(request.Start, neighbor, range))
                    continue;

                request.CameFrom[neighbor] = current;
                request.CostSoFar[neighbor] = cost;
                request.Frontier.Add((cost, neighbor));
            }
        }

        if (WithinWanderRadius(request.Start, startNode, range))
            return CompleteWander(request, random.Pick(request.CostSoFar).Key, range);

        // An off-center actor can explore from its containing polygon, but its center may not be a legal endpoint.
        var candidates = request.CostSoFar.Count - 1;
        if (candidates == 0)
            return PathResult.NoPath;

        var pick = random.Next(candidates);
        foreach (var (node, _) in request.CostSoFar)
        {
            if (!ReferenceEquals(node, startNode) && pick-- == 0)
                return CompleteWander(request, node, range);
        }

        throw new InvalidOperationException("The wander search lost its selected endpoint.");
    }

    private PathResult CompleteWander(BFSPathRequest request, PathPoly endpoint, float range)
    {
        if (CompleteSearch(request, endpoint) != PathResult.Path)
            return PathResult.NoPath;

        // Portal endpoints may move relative to the start grid between search slices.
        for (var i = 1; i < request.Polys.Count; i++)
        {
            if (WithinWanderRadius(request.Start, request.Polys[i], range))
                continue;

            request.Polys.Clear();
            return PathResult.NoPath;
        }

        return PathResult.Path;
    }

    private bool WithinWanderRadius(EntityCoordinates start, PathPoly node, float range)
    {
        return start.TryDelta(EntityManager, _transform, node.Coordinates, out var delta) &&
            delta.LengthSquared() <= range * range;
    }
    // End Frontier
}
