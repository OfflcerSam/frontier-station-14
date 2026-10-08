// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.NodeContainer.Nodes;
using Content.Shared.Atmos;
using Robust.Shared.Maths;

namespace Content.Server._NF.Power.Components;

/// <summary>Fixed primary-layer ports on a generator's footprint.</summary>
[RegisterComponent]
public sealed partial class GeneratorPipingComponent : Component
{
    [DataField] public string[] ExhaustNodes = { "exhaust" };
    [DataField] public string[] IntakeNodes = { "intake" };
    [DataField] public string[] FuelNodes = Array.Empty<string>();
    [DataField] public Dictionary<string, PipeDirection> PortDirections = new();
    /// <summary>Adjacent pipe tiles relative to the chassis origin, before rotation.</summary>
    [DataField] public Dictionary<string, Vector2i> PortOffsets = new();
    [DataField] public float ExhaustThrottlePressure = 300f;
    [DataField] public float ExhaustShutdownPressure = 600f;
    public HashSet<(PipeNode Port, PipeNode External)> Connections = new();
}
