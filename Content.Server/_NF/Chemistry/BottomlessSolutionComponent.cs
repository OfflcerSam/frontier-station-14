// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Chemistry.Components;

namespace Content.Server._NF.Chemistry;

/// <summary>Mapping/testing container that reproduces its first liquid sample.</summary>
[RegisterComponent]
public sealed partial class BottomlessSolutionComponent : Component
{
    [DataField]
    public string SolutionName = "beaker";

    [DataField]
    public Solution? Sample;

    public bool Refilling;
}
