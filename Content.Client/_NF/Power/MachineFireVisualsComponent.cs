// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Content.Client._NF.Power;

/// <summary>Independent, unscaled fire visuals for each occupied machine tile.</summary>
[RegisterComponent]
public sealed partial class MachineFireVisualsComponent : Component
{
    public readonly List<EntityUid> Fires = new();
}
