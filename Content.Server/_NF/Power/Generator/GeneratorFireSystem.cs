// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server._NF.Power.FuelModules;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Destructible;
using Content.Server.Power.Generator;
using Content.Shared._NF.Power.FuelModules;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Power.Generator;

namespace Content.Server._NF.Power.Generator;

/// <summary>Ignites a leaking generator on a damaging hit if it has fuel and oxygen.</summary>
public sealed class GeneratorFireSystem : EntitySystem
{
    [Dependency] private readonly FlammableSystem _flammable = default!;
    [Dependency] private readonly FuelModuleSystem _fuelModules = default!;
    [Dependency] private readonly GeneratorSystem _generator = default!;
    [Dependency] private readonly DestructibleSystem _destructible = default!;
    [Dependency] private readonly AtmosphereSystem _atmosphere = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GeneratorFireComponent, DamageChangedEvent>(OnDamageChanged,
            before: new[] { typeof(DestructibleSystem) });
    }

    private void OnDamageChanged(Entity<GeneratorFireComponent> entity, ref DamageChangedEvent arguments)
    {
        if (!arguments.DamageIncreased || arguments.DamageDelta == null ||
            arguments.DamageDelta.GetTotal() <= FixedPoint2.Zero)
            return;
        TryIgniteGenerator(entity, arguments);
    }

    public void TryIgniteGenerator(Entity<GeneratorFireComponent> entity, DamageChangedEvent arguments)
    {
        if (!TryComp<FuelGeneratorComponent>(entity, out var generator) || !generator.On ||
            _generator.GetFuel(entity.Owner) <= 0f ||
            !TryComp<FlammableComponent>(entity, out var flammable) || flammable.OnFire ||
            !_destructible.TryGetDestroyedAt(entity.Owner, out var destructionThreshold) ||
            destructionThreshold <= FixedPoint2.Zero ||
            _atmosphere.GetContainingMixture(entity.Owner, false, true) is not { } environment ||
            environment.GetMoles(Gas.Oxygen) < 1f)
            return;

        var fuelModule = _fuelModules.GetInstalledModule(entity.Owner);
        var ignitionThreshold = fuelModule is { } &&
                                Comp<FuelModuleComponent>(fuelModule.Value).Kind == FuelModuleKind.Solid
            ? entity.Comp.SolidIgnitionDamageFraction
            : entity.Comp.IgnitionDamageFraction;
        var damageFraction = arguments.Damageable.TotalDamage.Float() / destructionThreshold.Value.Float();
        if (damageFraction < ignitionThreshold)
            return;
        _flammable.AdjustFireStacks(entity.Owner, 2f, flammable);
        _flammable.Ignite(entity.Owner, entity.Owner, flammable);
        EntityManager.System<FuelPuddleFireSystem>().IgniteAt(entity.Owner);
    }
}
