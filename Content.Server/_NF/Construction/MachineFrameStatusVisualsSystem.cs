// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Construction;
using Content.Server.Construction.Components;
using Content.Shared._NF.Construction;

namespace Content.Server._NF.Construction;

/// <summary>Uses the real frame requirements to display wiring, board and completed-parts stages.</summary>
public sealed class MachineFrameStatusVisualsSystem : EntitySystem
{
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] private readonly MachineFrameSystem _frames = default!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var query = EntityQueryEnumerator<MachineFrameStatusVisualsComponent, AppearanceComponent>();
        while (query.MoveNext(out var uid, out var visuals, out var appearance))
        {
            var stage = visuals.InitialState;
            if (TryComp<MachineFrameComponent>(uid, out var frame))
                stage = !frame.HasBoard ? "wired" : _frames.IsComplete(frame) ? "ready" : "board";
            _appearance.SetData(uid, MachineFrameStatusVisuals.Stage, stage, appearance);
        }
    }
}
