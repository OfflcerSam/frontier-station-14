using Content.Server._Starlight.Plumbing.NodeGroups;
using Content.Server._Starlight.Plumbing.EntitySystems;
using Content.Server._Starlight.Plumbing.Components;
using Content.Server.NodeContainer.Nodes;
using Content.Shared._Starlight.Plumbing.Components;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.NodeContainer;
using Content.Shared.Tag;
using Robust.Shared.Prototypes;
using Robust.Shared.Map.Components;
using Robust.Shared.Map;

namespace Content.Server._Starlight.Plumbing.Nodes;

/// <summary>
///     A pipe node for plumbing systems using reagents instead of gases.
///     Extends PipeNode to reuse all pipe connection logic (direction, color, layer matching).
///     The only difference is it uses PlumbingNet instead of PipeNet.
/// </summary>
[DataDefinition]
[Virtual]
public partial class PlumbingNode : PipeNode
{
    private static readonly ProtoId<TagPrototype> _plumbingDuctTag = "PlumbingDuct";

    /// <summary>
    ///     The <see cref="IPlumbingNet"/> this plumbing duct is part of.
    /// </summary>
    [ViewVariables]
    public IPlumbingNet? PlumbingNet => (IPlumbingNet?) NodeGroup;

    public override void Initialize(EntityUid owner, IEntityManager entMan)
    {
        base.Initialize(owner, entMan);

        if (entMan.HasComponent<PlumbingManifoldComponent>(owner))
            return;

        if (entMan.TryGetComponent<AtmosPipeLayersComponent>(owner, out var layers))
            CurrentPipeLayer = layers.CurrentPipeLayer;
    }

    // Frontier: adapt to the node traversal and grid APIs in this engine version.
    public override IEnumerable<Node> GetReachableNodes(
        TransformComponent xform,
        EntityQuery<NodeContainerComponent> nodeQuery,
        EntityQuery<TransformComponent> xformQuery,
        MapGridComponent? grid,
        IEntityManager entMan)
    {
        if (!xform.Anchored || grid == null || xform.GridUid is not { } gridUid)
            yield break;

        var tags = entMan.System<TagSystem>();
        var isDuct = tags.HasTag(Owner, _plumbingDuctTag);
        var yielded = new HashSet<Node>();
        var nodeName = Name ?? string.Empty;
        var manifolds = entMan.System<PlumbingManifoldSystem>();
        if (manifolds.TryGetBridgedNodes(Owner, nodeName, nodeQuery, out var bridged))
        {
            foreach (var sibling in bridged)
            {
                if (sibling != this && yielded.Add(sibling))
                    yield return sibling;
            }
        }

        // Optional internal outlet linking.
        // When enabled on PlumbingOutletComponent, all configured outlet nodes on the same
        // machine are connected together in the graph so attached duct networks bridge.
        if (!isDuct &&
            entMan.TryGetComponent<PlumbingOutletComponent>(Owner, out var outlet) &&
            outlet.ConnectedOutlets &&
            IsConfiguredOutletNode(nodeName, outlet) &&
            nodeQuery.TryGetComponent(Owner, out var ownerContainer))
        {
            foreach (var (siblingName, siblingNode) in ownerContainer.Nodes)
            {
                if (siblingNode is PlumbingNode && siblingNode != this &&
                    IsConfiguredOutletNode(siblingName, outlet) && yielded.Add(siblingNode))
                {
                    yield return siblingNode;
                }
            }
        }

        var position = entMan.System<SharedMapSystem>().TileIndicesFor(gridUid, grid, xform.Coordinates);
        foreach (var direction in GetCardinalDirections(CurrentPipeDirection))
        {
            if (!isDuct)
            {
                var neighbor = FindMachineConnection(position, direction, grid, nodeQuery, tags, entMan);
                if (neighbor != null && yielded.Add(neighbor))
                    yield return neighbor;

                continue;
            }

            var opposite = direction.GetOpposite();
            foreach (var pipe in PipesInDirection(position, direction, grid, nodeQuery))
            {
                if (pipe is not PlumbingNode neighbor || !neighbor.CurrentPipeDirection.HasDirection(opposite))
                    continue;

                if (tags.HasTag(neighbor.Owner, _plumbingDuctTag))
                {
                    if (neighbor.CurrentPipeLayer == CurrentPipeLayer && yielded.Add(neighbor))
                        yield return neighbor;
                    continue;
                }

                // Node groups treat either direction as a connection. Both ends must pick the same duct.
                var machinePosition = position.Offset(direction.ToDirection());
                if (neighbor.FindMachineConnection(machinePosition, opposite, grid, nodeQuery, tags, entMan) == this &&
                    yielded.Add(neighbor))
                {
                    yield return neighbor;
                }
            }
        }
    }

    private PlumbingNode? FindMachineConnection(
        Vector2i position,
        PipeDirection direction,
        MapGridComponent grid,
        EntityQuery<NodeContainerComponent> nodes,
        TagSystem tags,
        IEntityManager entityManager)
    {
        PlumbingNode? selectedDuct = null;
        PlumbingNode? adjacentMachine = null;
        var opposite = direction.GetOpposite();
        foreach (var pipe in PipesInDirection(position, direction, grid, nodes))
        {
            if (pipe is not PlumbingNode neighbor || !neighbor.Connectable(entityManager) ||
                !neighbor.CurrentPipeDirection.HasDirection(opposite))
                continue;

            if (!tags.HasTag(neighbor.Owner, _plumbingDuctTag))
            {
                adjacentMachine ??= neighbor;
                continue;
            }

            // Prefer the primary duct, then the nearest alternative layer.
            if (selectedDuct == null || neighbor.CurrentPipeLayer < selectedDuct.CurrentPipeLayer)
                selectedDuct = neighbor;
        }

        return selectedDuct ?? adjacentMachine;
    }

    private static IEnumerable<PipeDirection> GetCardinalDirections(PipeDirection directions)
    {
        if (directions.HasDirection(PipeDirection.North))
            yield return PipeDirection.North;
        if (directions.HasDirection(PipeDirection.South))
            yield return PipeDirection.South;
        if (directions.HasDirection(PipeDirection.East))
            yield return PipeDirection.East;
        if (directions.HasDirection(PipeDirection.West))
            yield return PipeDirection.West;
    }

    private static bool IsConfiguredOutletNode(string nodeName, PlumbingOutletComponent outlet)
    {
        foreach (var configuredName in outlet.OutletNames)
        {
            if (nodeName.Equals(configuredName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
