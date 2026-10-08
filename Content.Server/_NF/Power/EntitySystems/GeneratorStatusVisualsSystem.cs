// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.Atmos.EntitySystems;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Server._NF.Power.Isotope;
using Content.Server._NF.Power.FuelModules;
using Content.Server._NF.Power.Steam;
using Content.Shared._NF.Power.FuelModules;
using Content.Shared._NF.Power.Isotope;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Containers.ItemSlots;
using Content.Server.Power.Components;
using Content.Server.Power.Generator;
using Content.Shared._NF.Power;
using Content.Shared.Damage;
using Content.Shared.Power.Generator;
using Content.Shared.Wires;

namespace Content.Server._NF.Power.EntitySystems;

/// <summary>Publishes independent operating, service-panel and casing-damage indicators.</summary>
public sealed class GeneratorStatusVisualsSystem : EntitySystem
{
    [Dependency] private readonly FuelModuleSystem _modules = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private readonly ItemSlotsSystem _slots = default!;
    [Dependency] private readonly SteamTurbineSystem _steam = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesAfter.Add(typeof(GeneratorSystem));
        UpdatesAfter.Add(typeof(FlammableSystem));
        UpdatesAfter.Add(typeof(IsotopeGeneratorSystem));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var query = EntityQueryEnumerator<GeneratorStatusVisualsComponent, AppearanceComponent>();
        while (query.MoveNext(out var uid, out _, out var appearance))
        {
            var running = TryComp<FuelGeneratorComponent>(uid, out var fuel)
                ? fuel.On
                : TryComp<PowerSupplierComponent>(uid, out var supplier) && supplier.Enabled;
            _appearance.SetData(uid, GeneratorVisuals.Running, running, appearance);
            _appearance.SetData(uid, GeneratorStatusVisuals.PanelOpen,
                TryComp<WiresPanelComponent>(uid, out var panel) && panel.Open, appearance);
            _appearance.SetData(uid, GeneratorStatusVisuals.Damaged,
                TryComp<DamageableComponent>(uid, out var damage) && damage.TotalDamage > 0, appearance);
            UpdateSupplies(uid, appearance);
            UpdateFire(uid, appearance);
            if (EntityManager.System<StationaryGeneratorDiagnosticsSystem>().GetData(uid) is { } diagnostics)
            {
                _appearance.SetData(uid, GeneratorStatusVisuals.Oxygen, diagnostics.OxygenState, appearance);
                _appearance.SetData(uid, GeneratorStatusVisuals.Exhaust, diagnostics.ExhaustState, appearance);
                _appearance.SetData(uid, GeneratorStatusVisuals.Rotor, diagnostics.RotorState, appearance);
                _appearance.SetData(uid, GeneratorStatusVisuals.Trip, diagnostics.Tripped, appearance);
            }
            if (TryComp<SteamTurbineComponent>(uid, out var steam))
            {
                _appearance.SetData(uid, GeneratorStatusVisuals.WaterLevel,
                    GetLevelState(_steam.GetWaterLevel((uid, steam)) / Math.Max(1f, steam.WaterCapacity)), appearance);
                _appearance.SetData(uid, GeneratorStatusVisuals.PressureLevel,
                    steam.SteamPressure <= 0f ? "cold" : steam.SteamPressure < 1f ? "low" :
                    steam.SteamPressure > 1.2f ? "high" : "normal", appearance);
                _appearance.SetData(uid, GeneratorStatusVisuals.TemperatureLevel,
                    steam.BoilerTemperature < steam.BoilingTemperature ? "cold" :
                    steam.BoilerTemperature < steam.RatedSteamTemperature ? "low" :
                    steam.BoilerTemperature >= steam.MaximumBoilerTemperature * 0.9f ? "high" : "normal", appearance);
            }
            if (TryComp<IsotopeGeneratorComponent>(uid, out var isotope))
                UpdateIsotopeBays(uid, isotope, panel?.Open == true, appearance);
        }
    }

    public void UpdateFire(EntityUid uid, AppearanceComponent appearance)
    {
        var stacks = TryComp<FlammableComponent>(uid, out var casing) && casing.OnFire ? casing.FireStacks : 0f;
        if (_modules.GetInstalledModule(uid) is { } module && TryComp<FlammableComponent>(module, out var fuel) && fuel.OnFire)
            stacks = Math.Max(stacks, fuel.FireStacks);
        // Burning installed fuel is visible across the host footprint, without adding casing damage or heat.
        _appearance.SetData(uid, FireVisuals.OnFire, stacks > 0f, appearance);
        _appearance.SetData(uid, FireVisuals.FireStacks, stacks, appearance);
    }

    private void UpdateSupplies(EntityUid uid, AppearanceComponent appearance)
    {
        var kind = "none";
        var fraction = 0f;
        if (_modules.GetInstalledModule(uid) is { } module && TryComp<FuelModuleComponent>(module, out var fuel))
        {
            kind = fuel.Kind == FuelModuleKind.Solid ? "solid" : "liquid";
            fraction = fuel.Kind == FuelModuleKind.Solid
                ? fuel.FractionalFuel.Values.Sum() / Math.Max(1f, fuel.BaseCapacity * (1f + 0.2f * (fuel.MatterBinRating - 1f)))
                : GetLiquidFuelFraction(module);
        }
        else if (HasComp<Content.Server._NF.Power.Generator.PipedGasFuelComponent>(uid))
        {
            kind = "gas";
            fraction = EntityManager.System<Content.Server._NF.Power.Generator.PipedGasFuelSystem>().GetFuel(uid) > 0f ? 1f : 0f;
        }
        else if (HasComp<ChemicalFuelGeneratorAdapterComponent>(uid))
        {
            kind = "liquid";
            fraction = GetLiquidFuelFraction(uid);
        }
        _appearance.SetData(uid, GeneratorStatusVisuals.FuelKind, kind, appearance);
        _appearance.SetData(uid, GeneratorStatusVisuals.FuelLevel, GetLevelState(fraction), appearance);
    }

    private float GetLiquidFuelFraction(EntityUid uid)
    {
        if (!TryComp<ChemicalFuelGeneratorAdapterComponent>(uid, out var adapter) ||
            !_solutions.TryGetSolution(uid, adapter.SolutionName, out _, out var solution))
            return 0f;

        // The lamp measures usable fuel volume, not contaminants or fuel-energy multipliers.
        var amount = adapter.Reagents.Keys.Sum(reagent => solution.GetTotalPrototypeQuantity(reagent).Float());
        return amount / Math.Max(1f, solution.MaxVolume.Float());
    }

    private static string GetLevelState(float fraction) =>
        fraction <= 0f ? "empty" : fraction < 0.25f ? "low" : fraction < 0.75f ? "medium" : "full";

    private void UpdateIsotopeBays(EntityUid uid, IsotopeGeneratorComponent isotope, bool panelOpen, AppearanceComponent appearance)
    {
        var state = "closed";
        if (panelOpen)
        {
            var shielded = TryComp<RadiationShieldingComponent>(uid, out var shielding) &&
                _slots.GetItemOrNull(uid, shielding.ShieldingSlot) != null;
            var first = isotope.CellSlots.Count > 0 && _slots.GetItemOrNull(uid, isotope.CellSlots[0]) != null;
            var second = isotope.CellSlots.Count > 1 && _slots.GetItemOrNull(uid, isotope.CellSlots[1]) != null;
            state = shielded ? "shielded" : isotope.CellSlots.Count <= 1 ? first ? "full1" : "empty1" :
                first ? second ? "full2" : "left2" : second ? "right2" : "empty2";
        }
        _appearance.SetData(uid, GeneratorStatusVisuals.IsotopeBays, state, appearance);
    }
}
