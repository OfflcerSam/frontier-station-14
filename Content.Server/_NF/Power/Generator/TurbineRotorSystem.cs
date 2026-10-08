// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using Content.Server._NF.Power.Components;
using Content.Server._NF.Power.EntitySystems;
using Content.Server.Destructible;
using Content.Server.Power.Components;
using Content.Server.Power.Generator;
using Content.Shared.Damage;
using Content.Shared.Examine;
using Content.Shared.Power.Generator;

namespace Content.Server._NF.Power.Generator;

public sealed class TurbineRotorSystem : EntitySystem
{
    [Dependency] private readonly GeneratorSystem _generator = default!;
    [Dependency] private readonly DestructibleSystem _destructible = default!;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesBefore.Add(typeof(GeneratorSystem));
        SubscribeLocalEvent<TurbineRotorComponent, GeneratorBeforeFuelBurnEvent>(OnBefore,
            before: new[] { typeof(GeneratorPipingSystem), typeof(StationaryGeneratorSystem), typeof(PipedGasFuelSystem) });
        SubscribeLocalEvent<TurbineRotorComponent, GeneratorStartAttemptEvent>(OnStart);
        SubscribeLocalEvent<TurbineRotorComponent, ExaminedEvent>(OnExamine);
    }

    public float DamageFraction(EntityUid uid) =>
        TryComp<DamageableComponent>(uid, out var damage) && _destructible.TryGetDestroyedAt(uid, out var threshold) && threshold > 0
            ? damage.TotalDamage.Float() / threshold.Value.Float() : 0f;

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<TurbineRotorComponent, FuelGeneratorComponent, PowerSupplierComponent>();
        while (query.MoveNext(out var uid, out var rotor, out var fuel, out var supplier))
            UpdateRotor((uid, rotor), fuel, supplier, frameTime);
    }

    public void UpdateRotor(Entity<TurbineRotorComponent> ent, FuelGeneratorComponent fuel,
        PowerSupplierComponent supplier, float frameTime)
    {
        var rotor = ent.Comp;
        var maximum = Math.Max(fuel.MaxTargetPower, 1f);
        var damage = DamageFraction(ent);
        if (fuel.On && !rotor.Tripped)
        {
            // A load loss transfers the rejected fraction into normalized rotor kinetic energy.
            // Squared speed is used so repeated tick subdivision does not multiply the impulse.
            var rejected = Math.Clamp((rotor.PreviousLoad - supplier.CurrentSupply) / maximum, 0f, 1f);
            rotor.Rpm = MathF.Sqrt(rotor.Rpm * rotor.Rpm + rejected * rotor.MaximumOperatingRpm * rotor.MaximumOperatingRpm);
            if (rotor.Rpm >= rotor.MaximumOperatingRpm * rotor.TripRatio || damage >= 0.6f)
            {
                rotor.Tripped = true;
                _generator.SetFuelGeneratorOn(ent, false, fuel);
            }
        }
        rotor.PreviousLoad = fuel.On ? supplier.CurrentSupply : 0f;
        var target = TargetRpm(rotor, fuel);
        var step = rotor.MaximumOperatingRpm / Math.Max(rotor.ResponseSeconds, 0.01f) * Math.Max(frameTime, 0f);
        rotor.Rpm += Math.Clamp(target - rotor.Rpm, -step, step);
    }

    public static float TargetRpm(TurbineRotorComponent rotor, FuelGeneratorComponent fuel) =>
        fuel.On && !rotor.Tripped
            ? rotor.MaximumOperatingRpm * Math.Clamp(fuel.TargetPower / Math.Max(fuel.MaxTargetPower, 1f), 0f, 1f) : 0f;

    private void OnStart(Entity<TurbineRotorComponent> ent, ref GeneratorStartAttemptEvent args)
    {
        if (args.FailureMessage != null)
            return;
        if (DamageFraction(ent) >= (ent.Comp.Tripped ? 0.2f : 0.6f) ||
            ent.Comp.Tripped && ent.Comp.Rpm >= ent.Comp.MaximumOperatingRpm * 0.1f)
            args.FailureMessage = "turbine-rotor-restart-denied";
        else
            ent.Comp.Tripped = false;
    }

    private void OnBefore(Entity<TurbineRotorComponent> ent, ref GeneratorBeforeFuelBurnEvent args)
    {
        var damage = DamageFraction(ent);
        if (ent.Comp.Tripped || damage >= 0.6f || ent.Comp.Rpm >= ent.Comp.MaximumOperatingRpm * ent.Comp.TripRatio)
        {
            ent.Comp.Tripped = true;
            args.Cancelled = true;
            return;
        }
        var factor = 1f;
        if (damage >= 0.4f && TryComp<StationaryGeneratorComponent>(ent, out var rating) &&
            TryComp<FuelGeneratorComponent>(ent, out var fuel))
            factor = Math.Min(1f, 0.7f * rating.RatedPower / Math.Max(1f, fuel.TargetPower));
        if (ent.Comp.Rpm >= ent.Comp.MaximumOperatingRpm * ent.Comp.GovernorRatio)
            factor = 0f;
        args.PowerMultiplier *= factor;
        args.FuelUsed *= factor;
    }

    private void OnExamine(Entity<TurbineRotorComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;
        args.PushMarkup(Loc.GetString("turbine-rotor-gauge", ("rpm", Math.Round(ent.Comp.Rpm))));
        if (ent.Comp.Tripped)
            args.PushMarkup(Loc.GetString("turbine-rotor-tripped"));
        else if (ent.Comp.Rpm >= ent.Comp.MaximumOperatingRpm * ent.Comp.WarningRatio)
            args.PushMarkup(Loc.GetString("turbine-rotor-overspeed"));
        else if (DamageFraction(ent) >= 0.2f)
            args.PushMarkup(Loc.GetString("turbine-rotor-damaged"));
    }
}
