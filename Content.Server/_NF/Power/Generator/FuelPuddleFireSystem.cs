// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using Content.Server.Atmos.EntitySystems;
using Content.Server._NF.Power.EntitySystems;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Content.Shared.Fluids.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._NF.Power.Generator;

/// <summary>Burn real liquid fuel on its actual puddle tile; no artificial gas fuel or blast.</summary>
public sealed class FuelPuddleFireSystem : EntitySystem
{
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private readonly AtmosphereSystem _atmosphere = default!;
    [Dependency] private readonly FlammableSystem _flammable = default!;
    private float _elapsed;

    public override void Initialize()
    {
        UpdatesBefore.Add(typeof(FlammableSystem));
        SubscribeLocalEvent<PuddleComponent, IgnitedEvent>(OnIgnited);
    }

    private void OnIgnited(Entity<PuddleComponent> ent, ref IgnitedEvent args)
    {
        if (!TryComp<FlammableComponent>(ent, out var flame))
            return;
        if (!_solutions.TryGetSolution(ent.Owner, ent.Comp.SolutionName, out _, out var solution) ||
            FuelGaugeSystem.UsableLiquid(solution) <= 0f ||
            _atmosphere.GetContainingMixture(ent.Owner, false, true) is not { Immutable: false } air || air.GetMoles(Gas.Oxygen) < 1f)
            _flammable.Extinguish(ent, flame);
    }

    public static bool IsFuel(string id) => id is "WeldingFuel" or "Ethanol" or "Plasma";

    public override void Update(float frameTime)
    {
        _elapsed += frameTime;
        if (_elapsed < 0.25f)
            return;
        var elapsed = _elapsed;
        _elapsed = 0f;
        var query = EntityQueryEnumerator<PuddleComponent, FlammableComponent>();
        while (query.MoveNext(out var uid, out var puddle, out var flame))
            UpdatePuddle((uid, puddle), flame, elapsed);
    }

    public void UpdatePuddle(Entity<PuddleComponent> ent, FlammableComponent flame, float elapsed)
    {
        if (!_solutions.TryGetSolution(ent.Owner, ent.Comp.SolutionName, out var solutionEntity, out var solution))
            return;
        var fuel = FuelGaugeSystem.UsableLiquid(solution);
        var air = _atmosphere.GetContainingMixture(ent.Owner, false, true);
        if (fuel <= 0f || air is not { Immutable: false } || air.GetMoles(Gas.Oxygen) < 1f)
        {
            _flammable.Extinguish(ent, flame);
            if (fuel <= 0f)
                _flammable.AdjustFireStacks(ent, -Math.Max(0, flame.FireStacks), flame);
            return;
        }
        // Negative stacks from extinguishers retain the native wet cooldown.
        if (flame.FireStacks < 0f)
            return;
        _flammable.AdjustFireStacks(ent, 2f - flame.FireStacks, flame);
        if (!flame.OnFire && air.Temperature >= flame.MinIgnitionTemperature)
            _flammable.Ignite(ent, ent, flame);
        if (!flame.OnFire)
            return;
        var remaining = FixedPoint2.New(Math.Min(fuel, Math.Min(elapsed, air.GetMoles(Gas.Oxygen) / 0.5f)));
        var burned = FixedPoint2.Zero;
        foreach (var reagent in FuelGaugeSystem.LiquidFuels)
        {
            var amount = FixedPoint2.Min(remaining, solution.GetTotalPrototypeQuantity(reagent));
            if (amount <= FixedPoint2.Zero)
                continue;
            _solutions.RemoveReagent(solutionEntity!.Value, reagent, amount);
            burned += amount;
            remaining -= amount;
        }
        if (burned <= FixedPoint2.Zero)
            return;
        air.AdjustMoles(Gas.Oxygen, -burned.Float() * 0.5f);
        air.AdjustMoles(Gas.CarbonDioxide, burned.Float() * 0.5f);
        var heatCapacity = _atmosphere.GetHeatCapacity(air, true);
        if (heatCapacity > 0f)
            air.Temperature += burned.Float() * 5000f / heatCapacity;
        if (FuelGaugeSystem.UsableLiquid(solution) <= 0f || air.GetMoles(Gas.Oxygen) < 1f)
            _flammable.Extinguish(ent, flame);
    }

    public void IgniteAt(EntityUid source)
    {
        var xform = Transform(source);
        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return;
        var tile = grid.TileIndicesFor(xform.Coordinates);
        foreach (var uid in grid.GetAnchoredEntities(tile))
        {
            if (!TryComp<PuddleComponent>(uid, out var puddle) || !TryComp<FlammableComponent>(uid, out var flame) ||
                !_solutions.TryGetSolution(uid, puddle.SolutionName, out _, out var solution) || FuelGaugeSystem.UsableLiquid(solution) <= 0f ||
                _atmosphere.GetContainingMixture(uid, false, true) is not { Immutable: false } air || air.GetMoles(Gas.Oxygen) < 1f || flame.FireStacks < 0f)
                continue;
            _flammable.AdjustFireStacks(uid, 2f - flame.FireStacks, flame);
            _flammable.Ignite(uid, source, flame);
        }
    }

    public void ExtinguishTile(TileRef tile)
    {
        if (!TryComp<MapGridComponent>(tile.GridUid, out var grid))
            return;
        foreach (var uid in grid.GetAnchoredEntities(tile.GridIndices))
        {
            if (!HasComp<PuddleComponent>(uid) || !TryComp<FlammableComponent>(uid, out var flame))
                continue;
            _flammable.Extinguish(uid, flame);
            _flammable.AdjustFireStacks(uid, -10f - flame.FireStacks, flame);
        }
    }
}
