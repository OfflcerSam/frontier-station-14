// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Administration.Logs;
using Content.Server._NF.Explosion;
using Content.Shared._NF.Construction;
using Robust.Shared.Map.Components;
using Robust.Shared.Random;
using Content.Shared.Database;
using Content.Shared.Damage.Systems;
using Content.Server._NF.Power.Components;
using Content.Server._NF.Power.EntitySystems;
using Content.Server._NF.Power.FuelModules;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Components;
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
using System.Numerics;
using System.Collections.Generic;
using System.Linq;
using Content.Server.Temperature.Components;
using Content.Server.Temperature.Systems;
using Content.Shared.Stunnable;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.Server._NF.Power.Steam;

/// <summary>Burns module fuel to maintain boiler heat and turns available steam pressure into power.</summary>
public sealed class SteamTurbineSystem : SharedGeneratorSystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly DamageableSystem _damage = default!;
    [Dependency] private readonly GeneratorPipingSystem _piping = default!;
    [Dependency] private readonly TemperatureSystem _temperature = default!;
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
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;

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
            if (!steam.Ruptured)
                HeatRoom((entity, steam), frameTime);
            if (!generator.On || steam.Ruptured)
            {
                steam.BoilerTemperature += (293.15f - steam.BoilerTemperature) * Math.Clamp(steam.CoolingRate * frameTime, 0f, 1f);
                UpdateSteamPressure((entity, steam), 0f, frameTime);
                if (steam.SteamPressure < steam.PressureTripThreshold)
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
        EmitSteam(entity, waterLoss, true);

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
        UpdateSteamPressure(entity, targetPressure, frameTime);
        if (entity.Comp.SteamPressure >= entity.Comp.PressureTripThreshold)
            entity.Comp.WasPressurized = true;
        if (waterFraction <= 0.02f ||
            entity.Comp.WasPressurized && entity.Comp.SteamPressure < entity.Comp.PressureTripThreshold)
        {
            _generator.SetFuelGeneratorOn(entity, false, generator);
            return;
        }
        supplier.MaxSupply = Math.Min(generator.TargetPower, stationary.RatedPower * Math.Min(targetPressure, entity.Comp.SteamPressure));
    }

    public void UpdateSteamPressure(Entity<SteamTurbineComponent> entity, float targetPressure, float frameTime)
    {
        var steam = entity.Comp;
        if (steam.Ruptured || GetWaterLevel(entity) <= 0f)
        {
            steam.SteamPressure = 0f;
            return;
        }
        steam.SteamPressure += (targetPressure - steam.SteamPressure) *
            (1f - MathF.Exp(-steam.PressureRecoveryRate * frameTime));
        if (TryComp<DamageableComponent>(entity, out var damageable) &&
            _destructible.TryGetDestroyedAt(entity.Owner, out var destructionThreshold) &&
            destructionThreshold > FixedPoint2.Zero)
        {
            var damageFraction = damageable.TotalDamage.Float() / destructionThreshold.Value.Float();
            if (damageFraction >= steam.SteamLeakDamageFraction)
                steam.SteamPressure *= MathF.Exp(-steam.PressureLeakRate * damageFraction * frameTime);
        }
        steam.SteamPressure = Math.Clamp(steam.SteamPressure, 0f, steam.MaximumSteamPressure);
    }

    public float GetBurstIntensity(Entity<SteamTurbineComponent> entity) =>
        _explosion.RadiusToIntensity(GetBurstRadius(entity), entity.Comp.BurstIntensitySlope, entity.Comp.BurstMaximumIntensity);

    public float GetBurstRadius(Entity<SteamTurbineComponent> entity) =>
        ScalePressureValue(entity.Comp, entity.Comp.RatedBurstRadius, entity.Comp.MaximumBurstRadius);

    public float GetFlashRadius(Entity<SteamTurbineComponent> entity) =>
        ScalePressureValue(entity.Comp, entity.Comp.RatedFlashRadius, entity.Comp.MaximumFlashRadius);

    public float GetBreachChance(Entity<SteamTurbineComponent> entity) =>
        ScalePressureValue(entity.Comp, entity.Comp.RatedBreachChance, entity.Comp.MaximumBreachChance);

    private static float ScalePressureValue(SteamTurbineComponent steam, float rated, float maximum)
    {
        var pressure = Math.Clamp(steam.SteamPressure, 0f, steam.MaximumSteamPressure);
        return pressure <= 1f ? rated * pressure : rated + (maximum - rated) *
            (pressure - 1f) / Math.Max(0.001f, steam.MaximumSteamPressure - 1f);
    }

    public ExplosionAreaRestriction GetBurstAreaRestriction(Entity<SteamTurbineComponent> entity, bool flash)
    {
        var limits = new ExplosionAreaRestriction
        {
            DamageRadius = GetBurstRadius(entity),
            FlashRadius = flash ? GetFlashRadius(entity) : GetBurstRadius(entity),
            BreachGrid = Transform(entity).GridUid,
        };
        if (!entity.Comp.CanBreachHull || limits.BreachGrid is not { } grid ||
            !TryComp<MapGridComponent>(grid, out var gridComp) ||
            !TryComp<MachineFootprintComponent>(entity, out var footprint))
            return limits;

        foreach (var offset in footprint.Tiles)
        {
            if (!_random.Prob(GetBreachChance(entity)))
                continue;
            var coordinates = new EntityCoordinates(entity.Owner, new Vector2(offset.X, offset.Y) * gridComp.TileSize);
            limits.BreachTiles.Add(_map.TileIndicesFor(grid, gridComp, coordinates));
        }
        return limits;
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

    public float EmitSteam(Entity<SteamTurbineComponent> entity, float waterAmount, bool routineExhaust = false)
    {
        if (waterAmount < 0.01f ||
            !_solutionContainers.TryGetSolution(entity.Owner, entity.Comp.WaterSolution, out var waterSolution, out _))
            return 0f;
        var removed = _solutionContainers.RemoveReagent(waterSolution.Value, "Water",
            FixedPoint2.FromCents((int) MathF.Floor(waterAmount * 100f)));
        if (removed <= FixedPoint2.Zero)
            return 0f;
        var steamMixture = new GasMixture();
        steamMixture.SetMoles(Gas.WaterVapor, removed.Float() * entity.Comp.VaporMolesPerUnit);
        steamMixture.Temperature = Math.Max(entity.Comp.BoilingTemperature, entity.Comp.BoilerTemperature);
        if (routineExhaust)
        {
            EntityManager.System<GeneratorTelemetrySystem>().Record(entity, water: removed.Float());
            _piping.ReleaseExhaust(entity, steamMixture);
        }
        else
            ReleaseHotSteam(entity, steamMixture, removed.Float());
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

    private void ReleaseHotSteam(Entity<SteamTurbineComponent> entity, GasMixture steamMixture, float waterAmount)
    {
        var transform = Transform(entity);
        var environments = new List<GasMixture>();
        var center = _transform.ToMapCoordinates(new EntityCoordinates(entity.Owner, entity.Comp.ReleaseOffset));
        if (transform.GridUid is { } grid)
        {
            var origin = _transform.GetGridOrMapTilePosition(entity.Owner, transform);
            // Spread a finite release over the chassis and its immediate surroundings, respecting walls.
            for (var x = -4; x <= 4; x++)
            for (var y = -4; y <= 4; y++)
            {
                var tile = origin + new Vector2i(x, y);
                var coordinates = new EntityCoordinates(grid, new Vector2(tile.X + 0.5f, tile.Y + 0.5f));
                if (!_atmosphere.IsTileAirBlocked(grid, tile) &&
                    EntityManager.System<SharedInteractionSystem>().InRangeUnobstructed(center,
                        _transform.ToMapCoordinates(coordinates), entity.Comp.Ruptured ? entity.Comp.SteamRuptureRadius : entity.Comp.SteamLeakRadius,
                        predicate: obstacle => obstacle == entity.Owner) &&
                    _atmosphere.GetTileMixture(grid, transform.MapUid, tile, true) is { Immutable: false } air &&
                    !environments.Any(environment => ReferenceEquals(environment, air)))
                    environments.Add(air);
            }
        }
        if (environments.Count == 0 &&
            _atmosphere.GetContainingMixture(entity.Owner, false, true) is { Immutable: false } fallback)
            environments.Add(fallback);
        var heatFraction = Math.Clamp((entity.Comp.BoilerTemperature - entity.Comp.BoilingTemperature) /
            (entity.Comp.RatedSteamTemperature - entity.Comp.BoilingTemperature), 0f, 1f);
        var remaining = environments.Count;
        foreach (var environment in environments)
            _atmosphere.Merge(environment, steamMixture.RemoveRatio(1f / remaining--));

        // Steam contact delivers stored release heat to exposed surfaces as well as the air.
        // Share one finite energy budget by heat capacity; insulation still uses normal temperature events.
        var surfaces = new List<(Entity<TemperatureComponent> Entity, float Capacity)>();
        var capacity = environments.Sum(environment => _atmosphere.GetHeatCapacity(environment, false));
        var radius = entity.Comp.Ruptured ? entity.Comp.SteamRuptureRadius : entity.Comp.SteamLeakRadius;
        foreach (var nearby in _lookup.GetEntitiesInRange<TemperatureComponent>(center, radius))
        {
            if (!HasComp<AtmosExposedComponent>(nearby) ||
                _atmosphere.GetContainingMixture(nearby.Owner) is not { } exposedAir ||
                !environments.Any(environment => ReferenceEquals(environment, exposedAir)) ||
                !EntityManager.System<SharedInteractionSystem>().InRangeUnobstructed(center,
                    _transform.GetMapCoordinates(nearby.Owner), radius,
                    predicate: obstacle => obstacle == entity.Owner || obstacle == nearby.Owner))
                continue;
            var surfaceCapacity = _temperature.GetHeatCapacity(nearby.Owner, nearby.Comp);
            if (surfaceCapacity <= 0f)
                continue;
            capacity += surfaceCapacity;
            surfaces.Add((nearby, surfaceCapacity));
        }
        var temperatureRise = waterAmount * entity.Comp.SteamReleaseHeat * heatFraction / Math.Max(capacity, 1f);
        foreach (var surface in surfaces)
        {
            var heat = Math.Min(temperatureRise,
                Math.Max(0f, entity.Comp.BoilerTemperature - surface.Entity.Comp.CurrentTemperature)) * surface.Capacity;
            if (heat > 0f)
                _temperature.ChangeHeat(surface.Entity.Owner, heat, temperature: surface.Entity.Comp);
        }
        foreach (var environment in environments)
        {
            var heat = Math.Min(temperatureRise,
                Math.Max(0f, entity.Comp.BoilerTemperature - environment.Temperature)) *
                _atmosphere.GetHeatCapacity(environment, true);
            if (heat > 0f)
                _atmosphere.AddHeat(environment, heat);
        }
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
        if (!_solutionContainers.TryGetSolution(entity.Owner, entity.Comp.WaterSolution, out var waterSolution, out var water))
            return false;
        if (water.AvailableVolume <= FixedPoint2.Zero)
        {
            _popup.PopupEntity(Loc.GetString("steam-turbine-water-full"), entity, user);
            return false;
        }
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
        arguments.PushMarkup(Loc.GetString("steam-turbine-pressure-gauge", ("pressure", Math.Round(entity.Comp.SteamPressure * entity.Comp.RatedPressureKpa))));
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
        // Read retained pressure BEFORE this hit's leaks. Prior hits have already vented their share.
        var catastrophic = arguments.DamageDelta.GetTotal().Float() >=
            destructionThreshold.Value.Float() * entity.Comp.RuptureDamageFraction &&
            entity.Comp.SteamPressure >= entity.Comp.PressureTripThreshold && GetWaterLevel(entity) > 0f;
        if (catastrophic || damageFraction >= 1f)
        {
            _adminLogger.Add(LogType.Damaged, catastrophic ? LogImpact.High : LogImpact.Medium,
                $"{ToPrettyString(arguments.Origin):actor} dealt {arguments.DamageDelta.GetTotal()} damage to {ToPrettyString(entity.Owner):subject}; steam rupture catastrophic={catastrophic}, pressure={entity.Comp.SteamPressure * entity.Comp.RatedPressureKpa} kPa(g), temperature={entity.Comp.BoilerTemperature} K.");
            RuptureSteam(entity, catastrophic, arguments.Origin);
            if (catastrophic && damageFraction < 1f)
            {
                var damageOrigin = arguments.Origin;
                // Run normal destruction/frame behaviors on the next update, outside this damage event.
                Robust.Shared.Timing.Timer.Spawn(TimeSpan.Zero, () =>
                {
                    if (Exists(entity.Owner) && !TerminatingOrDeleted(entity.Owner))
                        _damage.TryChangeDamage(entity.Owner,
                            new DamageSpecifier { DamageDict = new() { ["Blunt"] = destructionThreshold.Value } },
                            true, origin: damageOrigin);
                });
            }
            return;
        }
        if (entity.Comp.SteamPressure <= 0f)
            return;
        var previousDamageFraction = (arguments.Damageable.TotalDamage - arguments.DamageDelta.GetTotal()).Float() /
            destructionThreshold.Value.Float();
        var leakCount = DamageLeakSteps.Count(previousDamageFraction, damageFraction,
            entity.Comp.SteamLeakDamageFraction, entity.Comp.SteamLeakDamageStep);
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
        _adminLogger.Add(LogType.Damaged, LogImpact.Medium,
            $"{ToPrettyString(arguments.Origin):actor} caused {leakCount} steam leak steps on {ToPrettyString(entity.Owner):subject}.");
        entity.Comp.SteamPressure *= MathF.Pow(0.9f, (float) leakCount);
        entity.Comp.BoilerTemperature = Math.Max(293.15f, entity.Comp.BoilerTemperature - 4f * (float) leakCount);
    }

    public void RuptureSteam(Entity<SteamTurbineComponent> entity, bool catastrophic = true, EntityUid? origin = null)
    {
        if (entity.Comp.Ruptured)
            return;
        entity.Comp.Ruptured = true;
        StopSteamAudio(entity);
        var running = TryComp<FuelGeneratorComponent>(entity, out var generator) && generator.On;
        var burstPosition = _transform.ToMapCoordinates(new EntityCoordinates(entity.Owner, entity.Comp.ReleaseOffset));
        var flash = running && _generator.GetFuel(entity.Owner) > 0f &&
            _atmosphere.GetContainingMixture(entity.Owner, false, true) is { } burnerAir &&
            burnerAir.GetMoles(Gas.Oxygen) >= 1f;
        if (catastrophic && entity.Comp.SteamPressure >= entity.Comp.PressureTripThreshold && GetWaterLevel(entity) > 0f)
        {
            _adminLogger.Add(LogType.Explosion, LogImpact.High,
                $"{ToPrettyString(entity.Owner):subject} catastrophically ruptured at {entity.Comp.SteamPressure * entity.Comp.RatedPressureKpa} kPa(g), {entity.Comp.BoilerTemperature} K; source {ToPrettyString(origin):actor}.");
            var limits = GetBurstAreaRestriction(entity, flash);
            _explosion.QueueExplosion(burstPosition, "NFSteamPressureBurst", GetBurstIntensity(entity),
                entity.Comp.BurstIntensitySlope, entity.Comp.BurstMaximumIntensity, origin ?? entity.Owner,
                tileBreakScale: 15f,
                maxTileBreak: entity.Comp.CanBreachHull ? 2 : 1, canCreateVacuum: entity.Comp.CanBreachHull,
                areaRestriction: limits);
            ApplyPressureKnockdown(entity);
        }
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
        entity.Comp.SteamPressure = 0f;
        if (generator != null)
            _generator.SetFuelGeneratorOn(entity, false, generator);
    }

    public void HeatRoom(Entity<SteamTurbineComponent> entity, float frameTime)
    {
        if (_atmosphere.GetContainingMixture(entity.Owner, false, true) is not { Immutable: false } environment)
            return;
        // Casing heat follows the boiler/room gradient, including residual heat after shutdown.
        var heatFraction = Math.Max(0f, entity.Comp.BoilerTemperature - environment.Temperature) /
            (entity.Comp.RatedSteamTemperature - 293.15f);
        var heat = Math.Min(entity.Comp.CasingHeatPower * heatFraction * frameTime,
            Math.Max(0f, entity.Comp.BoilerTemperature - environment.Temperature) * _atmosphere.GetHeatCapacity(environment, false));
        if (heat > 0f)
            _atmosphere.AddHeat(environment, heat * _atmosphere.GetHeatCapacity(environment, true) /
                _atmosphere.GetHeatCapacity(environment, false));
    }

    private void ApplyPressureKnockdown(Entity<SteamTurbineComponent> entity)
    {
        var burstPosition = _transform.ToMapCoordinates(new EntityCoordinates(entity.Owner, entity.Comp.ReleaseOffset));
        var pressure = Math.Clamp(entity.Comp.SteamPressure, 0f, entity.Comp.MaximumSteamPressure);
        var radius = GetBurstRadius(entity);
        foreach (var nearby in _lookup.GetEntitiesInRange<CrawlerComponent>(burstPosition, radius))
        {
            if (!EntityManager.System<SharedInteractionSystem>().InRangeUnobstructed(burstPosition,
                    _transform.GetMapCoordinates(nearby.Owner), radius,
                    predicate: obstacle => obstacle == entity.Owner || obstacle == nearby.Owner))
                continue;
            _stun.TryKnockdown(nearby.Owner, TimeSpan.FromSeconds(Math.Clamp(pressure, 0.2f, 2f)));
        }
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
