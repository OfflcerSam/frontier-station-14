// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Numerics;
using Content.Shared._NF.Construction;
using Content.Shared.Atmos;
using Robust.Client.GameObjects;
using Robust.Shared.Map;

namespace Content.Client._NF.Power;

/// <summary>Reuse native fire/light visuals on separate child entities so chassis sprite scaling cannot distort flames.</summary>
public sealed class MachineFireVisualsSystem : VisualizerSystem<MachineFireVisualsComponent>
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MachineFireVisualsComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnShutdown(Entity<MachineFireVisualsComponent> ent, ref ComponentShutdown args) => Clear(ent.Comp);

    private void Clear(MachineFireVisualsComponent component)
    {
        foreach (var fire in component.Fires)
            if (!Deleted(fire)) Del(fire);
        component.Fires.Clear();
    }

    protected override void OnAppearanceChange(EntityUid uid, MachineFireVisualsComponent component, ref AppearanceChangeEvent args)
    {
        AppearanceSystem.TryGetData<bool>(uid, FireVisuals.OnFire, out var burning, args.Component);
        if (!burning)
        {
            Clear(component);
            return;
        }
        if (!TryComp<MachineFootprintComponent>(uid, out var footprint))
            return;
        if (component.Fires.Count == 0)
            foreach (var tile in footprint.Tiles)
                component.Fires.Add(Spawn("NFMachineTileFire", new EntityCoordinates(uid, new Vector2(tile.X, tile.Y))));
        AppearanceSystem.TryGetData<float>(uid, FireVisuals.FireStacks, out var stacks, args.Component);
        foreach (var fire in component.Fires)
        {
            AppearanceSystem.SetData(fire, FireVisuals.OnFire, true);
            AppearanceSystem.SetData(fire, FireVisuals.FireStacks, stacks);
        }
    }
}
