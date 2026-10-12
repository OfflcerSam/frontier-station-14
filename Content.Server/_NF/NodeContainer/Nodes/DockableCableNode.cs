// SPDX-FileCopyrightText: 2026 Alexander Grötzsch
// SPDX-FileCopyrightText: 2026 chilla55
//
// SPDX-License-Identifier: MPL-2.0 (Awaiting Confirmation)
//
// Adapted from 6th-Maroon-Division/Monolith,
// commit f84c9b2fe09e6d6e1839401346decd6b1300bcec.
using Content.Server.Power.Nodes;
using Content.Shared.NodeContainer;
using Robust.Shared.Map.Components;

namespace Content.Server.NodeContainer.Nodes;

/// <summary>
/// Rear terminal connection for a buffered powerlock.
/// Docked ports exchange stored energy without merging their cable networks.
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
            if (direction == rear && node is CableTerminalNode && node.NodeGroupID == NodeGroupID &&
                xformQuery.GetComponent(node.Owner).LocalRotation.GetCardinalDir().GetOpposite() == rear)
                yield return node;
        }
    }
}
