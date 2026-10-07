// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using Content.Server.Destructible;
using Content.Server.Fluids.EntitySystems;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;

namespace Content.Server._NF.Power.FuelModules;

public sealed class FuelLeakSystem : EntitySystem
{
    [Dependency] private readonly DestructibleSystem _destructible = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutionContainers = default!;
    [Dependency] private readonly PuddleSystem _puddles = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<FuelLeakComponent, DamageChangedEvent>(OnDamageChanged, before: new[] { typeof(DestructibleSystem) });
    }

    private void OnDamageChanged(Entity<FuelLeakComponent> entity, ref DamageChangedEvent arguments)
    {
        if (arguments.DamageIncreased && arguments.DamageDelta != null && arguments.DamageDelta.GetTotal() > FixedPoint2.Zero)
            UpdateLeak(entity, arguments);
    }

    public void UpdateLeak(Entity<FuelLeakComponent> entity, DamageChangedEvent arguments)
    {
        if (arguments.DamageDelta == null || entity.Comp.LeakDamageStep <= 0f ||
            !_destructible.TryGetDestroyedAt(entity.Owner, out var destructionThreshold) || destructionThreshold <= FixedPoint2.Zero)
            return;
        var damageable = arguments.Damageable;
        // The first loss occurs at the starting threshold; larger hits count every crossed mark.
        // Round away floating-point noise so exact 20%, 22%, ... boundaries remain stable.
        var quantity = Math.Max(0, 1 + Math.Floor(Math.Round(
            (Math.Min(damageable.TotalDamage.Float() / destructionThreshold.Value.Float(), 1f) - entity.Comp.LeakDamageFraction) / entity.Comp.LeakDamageStep, 5)))
            - Math.Max(0, 1 + Math.Floor(Math.Round(
            ((damageable.TotalDamage - arguments.DamageDelta.GetTotal()).Float() / destructionThreshold.Value.Float() - entity.Comp.LeakDamageFraction) / entity.Comp.LeakDamageStep, 5)));
        if (quantity <= 0)
            return;
        var module = EntityManager.System<FuelModuleSystem>().GetInstalledModule(entity) ?? entity.Owner;
        if (!_solutionContainers.TryGetSolution(module, entity.Comp.SolutionName, out var fuelSolution, out var solution))
            return;
        var leakVolume = FixedPoint2.Min(solution.Volume,
            FixedPoint2.New(solution.MaxVolume.Float() * entity.Comp.LeakCapacityFractionPerStep * quantity));
        if (leakVolume <= FixedPoint2.Zero)
            return;
        var fuel = _solutionContainers.SplitSolution(fuelSolution.Value, leakVolume);
        _puddles.TrySpillAt(Transform(entity).Coordinates, fuel, out _);
    }
}

