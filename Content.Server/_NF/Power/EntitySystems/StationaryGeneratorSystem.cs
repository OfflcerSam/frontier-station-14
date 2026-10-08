// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server._NF.Power.Components;
using Content.Server.Power.Components;
using Content.Server.Popups;
using Content.Shared.Atmos;
using Content.Server.Power.Generator;
using Robust.Shared.Timing;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Construction.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Power.Generator;

namespace Content.Server._NF.Power.EntitySystems;

/// <summary>
/// Applies FS14 stock-part ratings to stationary generators without the portable
/// supplier multiplier, so increased target output also increases fuel use.
/// </summary>
public sealed class StationaryGeneratorSystem : SharedGeneratorSystem
{
    [Dependency] private readonly SharedSolutionContainerSystem _solutionContainers = default!;

    [Dependency] private readonly GeneratorPipingSystem _piping = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly GeneratorSystem _generator = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<StationaryGeneratorComponent, GeneratorBeforeFuelBurnEvent>(OnBeforeFuelBurn);
        SubscribeLocalEvent<StationaryGeneratorComponent, GeneratorStartAttemptEvent>(OnStartAttempt);
        SubscribeLocalEvent<StationaryGeneratorComponent, RefreshPartsEvent>(OnRefreshParts);
        SubscribeLocalEvent<StationaryGeneratorComponent, UpgradeExamineEvent>(OnUpgradeExamine);
    }

    private void OnStartAttempt(Entity<StationaryGeneratorComponent> entity, ref GeneratorStartAttemptEvent arguments)
    {
        if (arguments.FailureMessage != null)
            return;

        if (_generator.GetIsClogged(entity))
            arguments.FailureMessage = "stationary-generator-contaminated";
        else if (_generator.GetFuel(entity) <= 0f)
            arguments.FailureMessage = "stationary-generator-empty";
        else if (TryComp<FuelGeneratorComponent>(entity, out var generator))
            arguments.FailureMessage = GetOxygenFailure(entity,
                generator.OptimalBurnRate * (float) _timing.TickPeriod.TotalSeconds /
                CalcFuelEfficiency(generator.TargetPower, generator.OptimalPower, generator));
    }

    private string? GetOxygenFailure(Entity<StationaryGeneratorComponent> entity, float FuelUsed)
    {
        if (entity.Comp.OxygenMolesPerFuelUnit <= 0f)
            return null;

        var atmosphere = _piping.GetIntakeMixture(entity.Owner);
        var oxygenRequired = FuelUsed * entity.Comp.OxygenMolesPerFuelUnit;
        return atmosphere == null || atmosphere.Immutable || atmosphere.GetMoles(Gas.Oxygen) < oxygenRequired
            ? "stationary-generator-no-oxygen"
            : null;
    }

    private void OnBeforeFuelBurn(Entity<StationaryGeneratorComponent> entity, ref GeneratorBeforeFuelBurnEvent arguments)
    {
        if (arguments.Cancelled || entity.Comp.OxygenMolesPerFuelUnit <= 0f)
            return;

        var oxygenRequired = arguments.FuelUsed * entity.Comp.OxygenMolesPerFuelUnit;
        var atmosphere = _piping.GetIntakeMixture(entity.Owner);
        if (atmosphere == null || atmosphere.Immutable || atmosphere.GetMoles(Gas.Oxygen) < oxygenRequired)
        {
            arguments.Cancelled = true;
            _popup.PopupEntity(Loc.GetString("stationary-generator-no-oxygen"), entity.Owner);
            return;
        }

        atmosphere.AdjustMoles(Gas.Oxygen, -oxygenRequired);
    }

    private void OnRefreshParts(Entity<StationaryGeneratorComponent> entity, ref RefreshPartsEvent arguments)
    {
        ApplyPartRatings(entity,
            arguments.PartRatings.GetValueOrDefault("Capacitor", 1f),
            arguments.PartRatings.GetValueOrDefault("Manipulator", 1f),
            arguments.PartRatings.GetValueOrDefault("MatterBin", 1f));
    }

    public void ApplyPartRatings(Entity<StationaryGeneratorComponent> entity,
        float capacitorRating, float manipulatorRating, float matterBinRating)
    {
        if (!TryComp<FuelGeneratorComponent>(entity, out var generator) ||
            !TryComp<PowerSupplierComponent>(entity, out var supplier))
            return;

        entity.Comp.CapacitorRating = Math.Clamp(capacitorRating, 1f, 4f);
        entity.Comp.ManipulatorRating = Math.Clamp(manipulatorRating, 1f, 4f);
        entity.Comp.MatterBinRating = Math.Clamp(matterBinRating, 1f, 4f);

        var targetMultiplier = 1f + 0.10f * (entity.Comp.CapacitorRating - 1f);
        var burnMultiplier = 1f - 0.05f * (entity.Comp.ManipulatorRating - 1f);
        var rampMultiplier = 1f + 0.15f * (entity.Comp.CapacitorRating - 1f);
        var capacityMultiplier = 1f + 0.20f * (entity.Comp.MatterBinRating - 1f);

        generator.OptimalPower = entity.Comp.RatedPower;
        generator.MaxTargetPower = entity.Comp.RatedMaximumPower + entity.Comp.RatedPower * (targetMultiplier - 1f);
        generator.TargetPower = Math.Clamp(generator.TargetPower, generator.MinTargetPower, generator.MaxTargetPower);
        generator.OptimalBurnRate = entity.Comp.RatedBurnRate * burnMultiplier;
        supplier.SupplyRampRate = entity.Comp.RatedRampRate * rampMultiplier;
        supplier.MaxSupply = generator.TargetPower;

        if (_solutionContainers.TryGetSolution(entity.Owner, entity.Comp.FuelSolution, out var fuelSolution))
        {
            var fuelCapacity = FixedPoint2.New(entity.Comp.RatedFuelCapacity * capacityMultiplier);
            _solutionContainers.SetCapacity(fuelSolution.Value, fuelCapacity);
        }
    }

    private void OnUpgradeExamine(Entity<StationaryGeneratorComponent> entity, ref UpgradeExamineEvent arguments)
    {
        arguments.AddPercentageUpgrade("stationary-generator-upgrade-target",
            1f + entity.Comp.RatedPower / entity.Comp.RatedMaximumPower * 0.10f * (entity.Comp.CapacitorRating - 1f));
        arguments.AddPercentageUpgrade("stationary-generator-upgrade-ramp",
            1f + 0.15f * (entity.Comp.CapacitorRating - 1f));
        arguments.AddPercentageUpgrade("stationary-generator-upgrade-fuel",
            1f - 0.05f * (entity.Comp.ManipulatorRating - 1f));
        if (!HasComp<Content.Shared._NF.Power.FuelModules.FuelModuleHostComponent>(entity))
            arguments.AddPercentageUpgrade("stationary-generator-upgrade-capacity",
            1f + 0.20f * (entity.Comp.MatterBinRating - 1f));
    }
}



