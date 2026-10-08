// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
using Content.Server._NF.Power.Components;
using Content.Server._NF.Power.EntitySystems;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Power.Generator;
using Content.Shared.Atmos;
using Content.Shared.Examine;

namespace Content.Server._NF.Power.Generator;

/// <summary>Checks both supplies before drawing either. Unused gases leave through the exhaust.</summary>
public sealed class PipedGasFuelSystem : EntitySystem
{
    [Dependency] private readonly GeneratorPipingSystem _piping = default!;
    [Dependency] private readonly AtmosphereSystem _atmos = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PipedGasFuelComponent, GeneratorGetFuelEvent>(OnGetFuel);
        SubscribeLocalEvent<PipedGasFuelComponent, GeneratorStartAttemptEvent>(OnStart);
        SubscribeLocalEvent<PipedGasFuelComponent, GeneratorBeforeFuelBurnEvent>(OnBefore,
            after: new[] { typeof(GeneratorPipingSystem), typeof(StationaryGeneratorSystem) });
        SubscribeLocalEvent<PipedGasFuelComponent, GeneratorUseFuel>(OnBurn);
        SubscribeLocalEvent<PipedGasFuelComponent, ExaminedEvent>(OnExamine);
    }

    public List<GasMixture> GetFuelSupplies(EntityUid uid) =>
        TryComp<GeneratorPipingComponent>(uid, out var piping)
            ? _piping.GetConnectedPorts((uid, piping), piping.FuelNodes) : new();

    public float GetFuel(EntityUid uid) => GetFuelSupplies(uid).Sum(x => x.GetMoles(Gas.Plasma));

    private void OnGetFuel(Entity<PipedGasFuelComponent> ent, ref GeneratorGetFuelEvent args) => args.Fuel = GetFuel(ent);

    public bool CanBurn(Entity<PipedGasFuelComponent> ent, float amount)
    {
        if (!float.IsFinite(amount) || amount <= 0f ||
            !TryComp<GeneratorPipingComponent>(ent, out var piping))
            return false;
        var fuel = GetFuelSupplies(ent);
        var air = _piping.GetIntakeMixtures(ent);
        var exhaust = _piping.GetConnectedPorts((ent.Owner, piping), true);
        // Ports on one network would otherwise feed their own exhaust back into the burner.
        if (fuel.Any(x => air.Contains(x) || exhaust.Contains(x)) || air.Any(exhaust.Contains))
            return false;
        return exhaust.Any(x => x.Pressure < piping.ExhaustShutdownPressure) &&
               fuel.Sum(x => x.GetMoles(Gas.Plasma)) >= amount &&
               air.Sum(x => x.GetMoles(Gas.Oxygen)) >= amount * ent.Comp.OxygenPerMole;
    }

    private void OnStart(Entity<PipedGasFuelComponent> ent, ref GeneratorStartAttemptEvent args)
    {
        if (args.FailureMessage == null && !CanBurn(ent, 0.001f))
            args.FailureMessage = "piped-gas-fuel-unavailable";
    }

    private void OnBefore(Entity<PipedGasFuelComponent> ent, ref GeneratorBeforeFuelBurnEvent args)
    {
        if (!args.Cancelled && args.FuelUsed > 0f && !CanBurn(ent, args.FuelUsed))
            args.Cancelled = true;
    }

    private void OnBurn(Entity<PipedGasFuelComponent> ent, ref GeneratorUseFuel args) => TryBurn(ent, args.FuelUsed);

    public bool TryBurn(Entity<PipedGasFuelComponent> ent, float amount)
    {
        if (!CanBurn(ent, amount))
            return false;
        var fuel = GetFuelSupplies(ent);
        var air = _piping.GetIntakeMixtures(ent);
        var fuelFraction = amount / fuel.Sum(x => x.GetMoles(Gas.Plasma));
        var airFraction = amount * ent.Comp.OxygenPerMole / air.Sum(x => x.GetMoles(Gas.Oxygen));
        var products = new GasMixture();
        foreach (var supply in fuel)
        {
            var parcel = supply.RemoveRatio(fuelFraction);
            parcel.SetMoles(Gas.Plasma, 0f);
            _atmos.Merge(products, parcel);
        }
        foreach (var supply in air)
        {
            var parcel = supply.RemoveRatio(airFraction);
            parcel.SetMoles(Gas.Oxygen, 0f);
            _atmos.Merge(products, parcel);
        }
        products.AdjustMoles(Gas.CarbonDioxide, amount);
        products.Temperature = Math.Max(products.Temperature, ent.Comp.ExhaustTemperature);
        _piping.ReleaseExhaust(ent, products);
        if (_atmos.GetContainingMixture(ent.Owner, false, true) is { Immutable: false } room)
            _atmos.AddHeat(room, amount * ent.Comp.CasingHeatPerMole);
        return true;
    }

    private void OnExamine(Entity<PipedGasFuelComponent> ent, ref ExaminedEvent args)
    {
        if (args.IsInDetailsRange)
            args.PushMarkup(Loc.GetString("piped-gas-fuel-gauge", ("amount", Math.Round(GetFuel(ent), 2))));
    }
}
