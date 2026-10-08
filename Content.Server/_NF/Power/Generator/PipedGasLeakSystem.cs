// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
using Content.Server._NF.Power.FuelModules;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Destructible;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Damage;
using Content.Shared.Power.Generator;
using Robust.Shared.Map.Components;

namespace Content.Server._NF.Power.Generator;

/// <summary>Damage releases a finite local pipe-volume equivalent, never a fraction of an entire network.</summary>
public sealed class PipedGasLeakSystem : EntitySystem
{
    [Dependency] private readonly PipedGasFuelSystem _fuel = default!;
    [Dependency] private readonly AtmosphereSystem _atmos = default!;
    [Dependency] private readonly DestructibleSystem _destructible = default!;
    [Dependency] private readonly FlammableSystem _flammable = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PipedGasLeakComponent, DamageChangedEvent>(OnDamage,
            before: new[] { typeof(DestructibleSystem) });
    }

    private void OnDamage(Entity<PipedGasLeakComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased || args.DamageDelta == null || ent.Comp.Ruptured ||
            !_destructible.TryGetDestroyedAt(ent.Owner, out var threshold) || threshold <= 0)
            return;
        var current = args.Damageable.TotalDamage.Float() / threshold.Value.Float();
        var previous = (args.Damageable.TotalDamage - args.DamageDelta.GetTotal()).Float() / threshold.Value.Float();
        var marks = DamageLeakSteps.Count(previous, current, ent.Comp.LeakDamageFraction, ent.Comp.LeakDamageStep);
        var volume = ent.Comp.LocalVolume * Math.Min(1f, marks * ent.Comp.LeakCapacityFractionPerStep);
        if (current >= 1f)
        {
            ent.Comp.Ruptured = true;
            volume = ent.Comp.LocalVolume;
        }
        if (volume <= 0f)
            return;
        var room = _atmos.GetContainingMixture(ent.Owner, false, true);
        if (room is not { Immutable: false })
            return;
        var supplies = _fuel.GetFuelSupplies(ent);
        var totalVolume = supplies.Sum(x => x.Volume);
        if (totalVolume <= 0f)
            return;
        var plasma = 0f;
        foreach (var supply in supplies)
        {
            var parcel = supply.RemoveRatio(Math.Clamp(volume / totalVolume, 0f, 1f));
            plasma += parcel.GetMoles(Gas.Plasma);
            _atmos.Merge(room, parcel);
        }
        var running = TryComp<FuelGeneratorComponent>(ent, out var generator) && generator.On;
        var burning = TryComp<FlammableComponent>(ent, out var flame) && flame.OnFire;
        if (plasma <= 0f || !(running || burning) || room.GetMoles(Gas.Oxygen) <= 0f)
            return;
        var transform = Transform(ent);
        if (TryComp<MapGridComponent>(transform.GridUid, out var grid))
            _atmos.HotspotExpose(transform.GridUid.Value, grid.TileIndicesFor(transform.Coordinates), 600f, volume, ent.Owner);
        if (flame != null)
        {
            _flammable.AdjustFireStacks(ent, 2f, flame);
            _flammable.Ignite(ent, ent, flame);
        }
    }
}
