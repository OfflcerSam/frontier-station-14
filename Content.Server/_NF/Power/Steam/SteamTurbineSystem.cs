// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server._NF.Power.Components;
using Content.Server._NF.Power.EntitySystems;
using Content.Server._NF.Power.FuelModules;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Destructible;
using Content.Server.Explosion.EntitySystems;
using Content.Server.Fluids.EntitySystems;
using Content.Server.Popups;
using Robust.Shared.Audio.Systems;
using Content.Server.Power.Components;
using Content.Server.Power.Generator;
using Content.Shared._NF.Power.FuelModules;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Construction.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction;
using Content.Shared.Power.Generator;
using Content.Shared.Wires;
using Robust.Shared.Timing;

namespace Content.Server._NF.Power.Steam;

/// <summary>Burns module fuel to maintain boiler heat and turns available steam pressure into power.</summary>
public sealed class SteamTurbineSystem : SharedGeneratorSystem
{
    [Dependency] private readonly AtmosphereSystem _atmosphere = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly DestructibleSystem _destructible = default!;
    [Dependency] private readonly ExplosionSystem _explosion = default!;
    [Dependency] private readonly FlammableSystem _flammable = default!;
    [Dependency] private readonly FuelModuleSystem _fuelModules = default!;
    [Dependency] private readonly GeneratorSystem _generator = default!;
    [Dependency] private readonly PuddleSystem _puddles = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutionContainers = default!;
    [Dependency] private readonly SolutionTransferSystem _solutionTransfer = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesBefore.Add(typeof(GeneratorSystem));
        SubscribeLocalEvent<SteamTurbineComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<SteamTurbineComponent, GeneratorUseFuel>(OnFuelUsed);
        SubscribeLocalEvent<SteamTurbineComponent, GeneratorStartAttemptEvent>(OnStartAttempt);
        SubscribeLocalEvent<SteamTurbineComponent, RefreshPartsEvent>(OnRefreshParts, after: new[] { typeof(StationaryGeneratorSystem) });
        SubscribeLocalEvent<SteamTurbineComponent, InteractUsingEvent>(OnInteractUsing, before: new[] { typeof(FuelModuleSystem) });
        SubscribeLocalEvent<SteamTurbineComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<SteamTurbineComponent, DamageChangedEvent>(OnDamageChanged, before: new[] { typeof(DestructibleSystem) });
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<SteamTurbineComponent, FuelGeneratorComponent, StationaryGeneratorComponent>();
        while (query.MoveNext(out var entity, out var steam, out var generator, out var stationary))
        {
            if (!generator.On || steam.Ruptured)
            {
                steam.BoilerTemperature += (293.15f - steam.BoilerTemperature) * Math.Clamp(steam.CoolingRate * frameTime, 0f, 1f);
                steam.SteamPressure = 0f;
                steam.WasPressurized = false;
                continue;
            }

            var fuelModule = _fuelModules.GetInstalledModule(entity);
            var heatFactor = fuelModule is { } ? GetFuelHeatFactor((fuelModule.Value, Comp<FuelModuleComponent>(fuelModule.Value)), steam) : 1f;
            var demandFraction = Math.Clamp(generator.TargetPower / stationary.RatedPower, 0.25f, steam.MaximumSteamPressure);
            var fuelPenalty = MathF.Pow(demandFraction, generator.FuelEfficiencyConstant);
            var heatDemand = Math.Max(0.3f + 0.7f * demandFraction, fuelPenalty);
            var burnMultiplier = 1f - 0.05f * (stationary.ManipulatorRating - 1f);
            generator.OptimalBurnRate = stationary.RatedBurnRate * burnMultiplier * heatDemand /
                (fuelPenalty * Math.Max(heatFactor, 0.1f));
            if (TryComp<GeneratorExhaustGasComponent>(entity, out var exhaust))
                exhaust.Temperature = Math.Max(373.15f, steam.BoilerTemperature);
        }
    }

    private void OnFuelUsed(Entity<SteamTurbineComponent> entity, ref GeneratorUseFuel arguments)
    {
        UpdateSteam(entity, arguments.FuelUsed, (float) _timing.TickPeriod.TotalSeconds);
    }

    public void UpdateSteam(Entity<SteamTurbineComponent> entity, float fuelUsed, float frameTime)
    {
        if (entity.Comp.Ruptured || !TryComp<FuelGeneratorComponent>(entity, out var generator) ||
            !TryComp<StationaryGeneratorComponent>(entity, out var stationary) ||
            !TryComp<PowerSupplierComponent>(entity, out var supplier))
            return;

        var fuelModule = _fuelModules.GetInstalledModule(entity);
        var heatFactor = fuelModule is { } ? GetFuelHeatFactor((fuelModule.Value, Comp<FuelModuleComponent>(fuelModule.Value)), entity.Comp) : 1f;
        var burnMultiplier = 1f - 0.05f * (stationary.ManipulatorRating - 1f);
        var thermalFuel = fuelUsed * heatFactor / burnMultiplier;
        entity.Comp.BoilerTemperature = Math.Clamp(
            entity.Comp.BoilerTemperature + thermalFuel * entity.Comp.HeatPerFuelUnit -
            (entity.Comp.BoilerTemperature - 293.15f) * entity.Comp.CoolingRate * frameTime,
            293.15f, entity.Comp.MaximumBoilerTemperature);

        entity.Comp.WaterLossRemainder += thermalFuel * entity.Comp.WaterLossRate / stationary.RatedBurnRate;
        var waterLoss = MathF.Floor(entity.Comp.WaterLossRemainder * 100f) / 100f;
        entity.Comp.WaterLossRemainder -= waterLoss;
        EmitSteam(entity, waterLoss);

        var waterFraction = 0f;
        if (_solutionContainers.TryGetSolution(entity.Owner, entity.Comp.WaterSolution, out _, out var water))
            waterFraction = GetWaterLevel(entity) / Math.Max(water.MaxVolume.Float(), 1f);
        var targetPressure = Math.Clamp((entity.Comp.BoilerTemperature - entity.Comp.BoilingTemperature) /
            (entity.Comp.RatedSteamTemperature - entity.Comp.BoilingTemperature), 0f, entity.Comp.MaximumSteamPressure);
        targetPressure *= Math.Clamp(waterFraction / entity.Comp.MinimumStartingWaterFraction, 0f, 1f);
        if (TryComp<DamageableComponent>(entity, out var damageable) &&
            _destructible.TryGetDestroyedAt(entity.Owner, out var destructionThreshold) &&
            destructionThreshold > FixedPoint2.Zero)
        {
            var damageFraction = Math.Clamp(damageable.TotalDamage.Float() / destructionThreshold.Value.Float(), 0f, 1f);
            if (damageFraction >= entity.Comp.SteamLeakDamageFraction)
                targetPressure *= 0.9f - 0.4f * (damageFraction - entity.Comp.SteamLeakDamageFraction) /
                    (1f - entity.Comp.SteamLeakDamageFraction);
        }
        entity.Comp.SteamPressure = targetPressure;
        if (targetPressure >= entity.Comp.PressureTripThreshold)
            entity.Comp.WasPressurized = true;
        if (GetWaterLevel(entity) <= 0f ||
            entity.Comp.WasPressurized && targetPressure < entity.Comp.PressureTripThreshold)
        {
            _generator.SetFuelGeneratorOn(entity, false, generator);
            return;
        }
        supplier.MaxSupply = Math.Min(generator.TargetPower, stationary.RatedPower * targetPressure);
    }

    public float GetFuelHeatFactor(Entity<FuelModuleComponent> fuelModule, SteamTurbineComponent steam)
    {
        var fuelTotal = 0f;
        var weightedHeat = 0f;
        if (fuelModule.Comp.Kind == FuelModuleKind.Solid)
        {
            foreach (var (materialId, materialUnits) in fuelModule.Comp.FractionalFuel)
            {
                var fuelValue = materialUnits * fuelModule.Comp.MaterialFuelValues.GetValueOrDefault(materialId);
                fuelTotal += fuelValue;
                weightedHeat += fuelValue * steam.SolidHeatFactors.GetValueOrDefault(materialId, 1f);
            }
        }
        else if (TryComp<ChemicalFuelGeneratorAdapterComponent>(fuelModule, out var adapter) &&
                 _solutionContainers.TryGetSolution(fuelModule.Owner, "tank", out _, out var solution))
        {
            foreach (var reagentQuantity in solution)
            {
                var reagentId = reagentQuantity.Reagent.Prototype;
                var fuelValue = reagentQuantity.Quantity.Float() * adapter.Reagents.GetValueOrDefault(reagentId);
                fuelTotal += fuelValue;
                weightedHeat += fuelValue * steam.LiquidHeatFactors.GetValueOrDefault(reagentId, 1f);
            }
        }
        return fuelTotal > 0f ? weightedHeat / fuelTotal : 1f;
    }

    public float GetWaterLevel(Entity<SteamTurbineComponent> entity)
    {
        return _solutionContainers.TryGetSolution(entity.Owner, entity.Comp.WaterSolution, out _, out var water)
            ? water.GetTotalPrototypeQuantity("Water").Float() : 0f;
    }

    public float EmitSteam(Entity<SteamTurbineComponent> entity, float waterAmount)
    {
        if (waterAmount < 0.01f ||
            !_solutionContainers.TryGetSolution(entity.Owner, entity.Comp.WaterSolution, out var waterSolution, out _))
            return 0f;
        var removed = _solutionContainers.RemoveReagent(waterSolution.Value, "Water",
            FixedPoint2.FromCents((int) MathF.Floor(waterAmount * 100f)));
        if (removed <= FixedPoint2.Zero)
            return 0f;
        var steamMixture = new GasMixture();
        steamMixture.SetMoles(Gas.WaterVapor, removed.Float());
        steamMixture.Temperature = Math.Max(entity.Comp.BoilingTemperature, entity.Comp.BoilerTemperature);
        if (_atmosphere.GetContainingMixture(entity.Owner, false, true) is { } environment)
        {
            var ventHeat = Math.Max(0f, steamMixture.Temperature - environment.Temperature) *
                removed.Float() * _atmosphere.GasSpecificHeats[(int) Gas.WaterVapor] *
                (entity.Comp.VentHeatMultiplier - 1f);
            _atmosphere.Merge(environment, steamMixture);
            if (ventHeat > 0f)
                _atmosphere.AddHeat(environment, ventHeat);
        }
        entity.Comp.VaporSinceReleaseSound += removed.Float();
        if (!entity.Comp.Ruptured && entity.Comp.VaporSinceReleaseSound >= 1f &&
            _timing.CurTime.TotalSeconds - entity.Comp.LastReleaseSoundTime >= 10d)
        {
            entity.Comp.VaporReleaseAudio = _audio.Stop(entity.Comp.VaporReleaseAudio);
            entity.Comp.VaporReleaseAudio = _audio.PlayPvs(entity.Comp.VaporReleaseSound, entity.Owner)?.Entity;
            entity.Comp.VaporSinceReleaseSound = 0f;
            entity.Comp.LastReleaseSoundTime = _timing.CurTime.TotalSeconds;
        }
        return removed.Float();
    }

    private void OnStartAttempt(Entity<SteamTurbineComponent> entity, ref GeneratorStartAttemptEvent arguments)
    {
        if (arguments.FailureMessage == null && GetWaterLevel(entity) <
            entity.Comp.WaterCapacity * entity.Comp.MinimumStartingWaterFraction)
            arguments.FailureMessage = "steam-turbine-low-water";
    }

    private void OnRefreshParts(Entity<SteamTurbineComponent> entity, ref RefreshPartsEvent arguments)
    {
        if (!_solutionContainers.TryGetSolution(entity.Owner, entity.Comp.WaterSolution, out var waterSolution, out var water))
            return;
        var capacityMultiplier = 1f + 0.2f * (Math.Clamp(arguments.PartRatings.GetValueOrDefault("MatterBin", 1f), 1f, 4f) - 1f);
        _solutionContainers.SetCapacity(waterSolution.Value,
            FixedPoint2.Max(FixedPoint2.New(entity.Comp.WaterCapacity * capacityMultiplier), water.Volume));
    }

    private void OnInteractUsing(Entity<SteamTurbineComponent> entity, ref InteractUsingEvent arguments)
    {
        if (arguments.Handled || !TryComp<SolutionTransferComponent>(arguments.Used, out var transfer) ||
            !_solutionContainers.TryGetDrainableSolution(arguments.Used, out _, out var source) ||
            source.GetTotalPrototypeQuantity("Water") <= FixedPoint2.Zero)
            return;
        arguments.Handled = true;
        TryFillWater(entity, arguments.Used, arguments.User, transfer.TransferAmount);
    }

    public bool TryFillWater(Entity<SteamTurbineComponent> entity, EntityUid source, EntityUid user, FixedPoint2 amount)
    {
        if (TryComp<WiresPanelComponent>(entity, out var panel) && panel.Open)
        {
            _popup.PopupEntity(Loc.GetString("steam-turbine-close-panel"), entity, user);
            return false;
        }
        if (!_solutionContainers.TryGetDrainableSolution(source, out var sourceSolution, out var contents) ||
            contents.GetTotalPrototypeQuantity("Water") != contents.Volume)
        {
            _popup.PopupEntity(Loc.GetString("steam-turbine-pure-water"), entity, user);
            return false;
        }
        if (!_solutionContainers.TryGetSolution(entity.Owner, entity.Comp.WaterSolution, out var waterSolution, out _))
            return false;
        if (_solutionTransfer.Transfer(user, source, sourceSolution.Value, entity.Owner, waterSolution.Value, amount) <= FixedPoint2.Zero)
            return false;
        _popup.PopupEntity(Loc.GetString("steam-turbine-water-poured", ("target", entity.Owner)), entity, user);
        return true;
    }

    private void OnExamined(Entity<SteamTurbineComponent> entity, ref ExaminedEvent arguments)
    {
        if (!arguments.IsInDetailsRange)
            return;
        var capacity = _solutionContainers.TryGetSolution(entity.Owner, entity.Comp.WaterSolution, out _, out var water)
            ? water.MaxVolume.Float() : entity.Comp.WaterCapacity;
        arguments.PushMarkup(Loc.GetString("steam-turbine-water-gauge", ("water", Math.Floor(GetWaterLevel(entity))),
            ("capacity", Math.Floor(capacity))));
        arguments.PushMarkup(Loc.GetString("steam-turbine-pressure-gauge", ("pressure", Math.Round(entity.Comp.SteamPressure * 100f))));
        arguments.PushMarkup(Loc.GetString("steam-turbine-temperature-gauge", ("temperature", Math.Round(entity.Comp.BoilerTemperature - 273.15f))));
    }

    private void OnDamageChanged(Entity<SteamTurbineComponent> entity, ref DamageChangedEvent arguments)
    {
        if (entity.Comp.Ruptured || !arguments.DamageIncreased || arguments.DamageDelta == null ||
            arguments.DamageDelta.GetTotal() <= FixedPoint2.Zero ||
            !_destructible.TryGetDestroyedAt(entity.Owner, out var destructionThreshold) ||
            destructionThreshold <= FixedPoint2.Zero)
            return;
        var damageFraction = arguments.Damageable.TotalDamage.Float() / destructionThreshold.Value.Float();
        if (damageFraction >= 1f)
        {
            RuptureSteam(entity);
            return;
        }
        if (!TryComp<FuelGeneratorComponent>(entity, out var generator) || !generator.On || entity.Comp.SteamPressure <= 0f)
            return;
        var previousDamageFraction = (arguments.Damageable.TotalDamage - arguments.DamageDelta.GetTotal()).Float() /
            destructionThreshold.Value.Float();
        var leakCount = Math.Max(0, 1 + Math.Floor(Math.Round(
            (damageFraction - entity.Comp.SteamLeakDamageFraction) / entity.Comp.SteamLeakDamageStep, 5))) -
            Math.Max(0, 1 + Math.Floor(Math.Round(
            (previousDamageFraction - entity.Comp.SteamLeakDamageFraction) / entity.Comp.SteamLeakDamageStep, 5)));
        if (leakCount <= 0)
            return;
        if (previousDamageFraction < entity.Comp.SteamLeakDamageFraction)
            entity.Comp.LastReleaseSoundTime = _timing.CurTime.TotalSeconds - 10d;
        if (EmitSteam(entity, entity.Comp.WaterCapacity * entity.Comp.SteamLeakCapacityFractionPerStep * (float) leakCount) > 0f &&
            entity.Comp.LastReleaseSoundTime == _timing.CurTime.TotalSeconds)
        {
            entity.Comp.LeakAudio = _audio.Stop(entity.Comp.LeakAudio);
            entity.Comp.LeakAudio = _audio.PlayPvs(entity.Comp.LeakSound, entity.Owner)?.Entity;
        }
        entity.Comp.SteamPressure *= MathF.Pow(0.9f, (float) leakCount);
        entity.Comp.BoilerTemperature = Math.Max(293.15f, entity.Comp.BoilerTemperature - 4f * (float) leakCount);
    }

    public void RuptureSteam(Entity<SteamTurbineComponent> entity)
    {
        if (entity.Comp.Ruptured)
            return;
        entity.Comp.Ruptured = true;
        StopSteamAudio(entity);
        var running = TryComp<FuelGeneratorComponent>(entity, out var generator) && generator.On;
        if (running && entity.Comp.SteamPressure >= entity.Comp.PressureTripThreshold)
            _explosion.QueueExplosion(entity.Owner, "Default", Math.Min(entity.Comp.MaximumBurstIntensity,
                entity.Comp.SteamPressure <= 1f
                    ? entity.Comp.RatedBurstIntensity * entity.Comp.SteamPressure
                    : entity.Comp.RatedBurstIntensity + (entity.Comp.MaximumBurstIntensity - entity.Comp.RatedBurstIntensity) *
                      (entity.Comp.SteamPressure - 1f) / (entity.Comp.MaximumSteamPressure - 1f)),
                5f, 10f, tileBreakScale: 0f, maxTileBreak: 0, canCreateVacuum: false);
        if (GetWaterLevel(entity) > 0f && entity.Comp.BoilerTemperature > entity.Comp.BoilingTemperature)
        {
            entity.Comp.LastReleaseSoundTime = _timing.CurTime.TotalSeconds - 10d;
            EmitSteam(entity, GetWaterLevel(entity));
        }
        else if (_solutionContainers.TryGetSolution(entity.Owner, entity.Comp.WaterSolution, out var waterSolution, out var water))
            _puddles.TrySpillAt(Transform(entity).Coordinates,
                _solutionContainers.SplitSolution(waterSolution.Value, water.Volume), out _);
        if (running && _fuelModules.GetInstalledModule(entity.Owner) is { } fuelModule &&
            _generator.GetFuel(entity.Owner) > 0f &&
            TryComp<FlammableComponent>(fuelModule, out var flammable) &&
            _atmosphere.GetContainingMixture(entity.Owner, false, true) is { } environment &&
            environment.GetMoles(Gas.Oxygen) >= 1f)
        {
            _flammable.AdjustFireStacks(fuelModule, 2f, flammable);
            _flammable.Ignite(fuelModule, entity.Owner, flammable);
        }
        if (running && Transform(entity).GridUid is { } grid)
        {
            var position = _transform.GetGridOrMapTilePosition(entity.Owner, Transform(entity));
            _atmosphere.HotspotExpose(grid, position, Math.Max(500f, entity.Comp.BoilerTemperature), 50f, entity.Owner, true);
        }
        if (generator != null)
            _generator.SetFuelGeneratorOn(entity, false, generator);
    }

    private void OnShutdown(Entity<SteamTurbineComponent> entity, ref ComponentShutdown arguments)
    {
        StopSteamAudio(entity);
    }

    private void StopSteamAudio(Entity<SteamTurbineComponent> entity)
    {
        entity.Comp.VaporReleaseAudio = _audio.Stop(entity.Comp.VaporReleaseAudio);
        entity.Comp.LeakAudio = _audio.Stop(entity.Comp.LeakAudio);
    }
}
