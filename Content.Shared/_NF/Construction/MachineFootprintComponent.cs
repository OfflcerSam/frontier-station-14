// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameStates;
using Robust.Shared.Maths;

namespace Content.Shared._NF.Construction;

/// <summary>Grid tiles occupied by a machine, relative to its origin tile.</summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class MachineFootprintComponent : Component
{
    [DataField(required: true)]
    public List<Vector2i> Tiles = new();
}
