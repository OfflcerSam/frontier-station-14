// SPDX-FileCopyrightText: 2026 Alexander Grötzsch
// SPDX-FileCopyrightText: 2026 chilla55
//
// SPDX-License-Identifier: MPL-2.0 (Awaiting Confirmation)
//
// Adapted from 6th-Maroon-Division/Monolith,
// commit f84c9b2fe09e6d6e1839401346decd6b1300bcec.
using Content.Server.Power.Nodes;
using Content.Server.Shuttles.Components;
using Content.Shared.NodeContainer;
using Robust.Shared.Map.Components;

namespace Content.Server.NodeContainer.Nodes;

/// <summary>
/// Connects matching cable behind a powerlock to its docked partner.
/// </summary>
[DataDefinition]
public sealed partial class DockableCableNode : Node, IRotatableNode
{
    public bool RotateNode(in MoveEvent ev) => true;

    public override IEnumerable<Node> GetReachableNodes(
        TransformComponent xform,
        EntityQuery<NodeContainerComponent> nodeQuery,
        EntityQuery<TransformComponent> xformQuery,
        MapGridComponent? grid,
        IEntityManager entMan)
    {
        if (!xform.Anchored || grid == null)
            yield break;

        var rear = xform.LocalRotation.GetCardinalDir().GetOpposite();
        var tile = grid.TileIndicesFor(xform.Coordinates);
        foreach (var (direction, node) in NodeHelpers.GetCardinalNeighborNodes(nodeQuery, grid, tile, false))
        {
            if (direction != rear || node is not CableNode cable || node.NodeGroupID != NodeGroupID)
                continue;

            // Let the cable apply its terminal-direction blocking rules as well.
            foreach (var reachable in cable.GetReachableNodes(xformQuery.GetComponent(cable.Owner), nodeQuery, xformQuery, grid, entMan))
            {
                if (reachable == this)
                    yield return cable;
            }
        }

        if (!entMan.TryGetComponent(Owner, out DockingComponent? docking) ||
            docking.DockedWith == null ||
            !nodeQuery.TryComp(docking.DockedWith.Value, out var otherNodeContainer))
        {
            yield break;
        }

        foreach (var node in otherNodeContainer.Nodes.Values)
        {
            if (node is DockableCableNode dockable && dockable.NodeGroupID == NodeGroupID)
                yield return dockable;
        }
    }
}
