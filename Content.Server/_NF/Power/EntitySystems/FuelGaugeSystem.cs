// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
using Content.Server.Power.Generator;
using Content.Server._NF.Power.FuelModules;
using Content.Shared._NF.Power.FuelModules;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Examine;
using Content.Shared.Materials;
using Content.Shared.Power.Generator;

namespace Content.Server._NF.Power.EntitySystems;

/// <summary>Physical fuel amounts on hosts, modules and ordinary fuel containers.</summary>
public sealed class FuelGaugeSystem : EntitySystem
{
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!;
    public static readonly string[] LiquidFuels = { "WeldingFuel", "Ethanol", "Plasma" };

    public override void Initialize()
    {
        SubscribeLocalEvent<SolutionContainerManagerComponent, ExaminedEvent>(OnContainer);
        SubscribeLocalEvent<MaterialStorageComponent, ExaminedEvent>(OnMaterials);
    }

    public void ModuleGauge(Entity<FuelModuleComponent> ent, ref ExaminedEvent args)
    {
        if (ent.Comp.Kind == FuelModuleKind.Solid)
            Gauge(ent.Comp.FractionalFuel.Values.Sum(), "fuel-module-solid-units", ref args);
        else if (_solutions.TryGetSolution(ent.Owner, "tank", out _, out var solution))
            Gauge(UsableLiquid(solution), "fuel-module-liquid-units", ref args);
    }
    public static float UsableLiquid(Solution solution) =>
        LiquidFuels.Sum(id => solution.GetTotalPrototypeQuantity(id).Float());

    private void OnContainer(Entity<SolutionContainerManagerComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange || HasComp<FuelModuleComponent>(ent) || HasComp<FuelModuleHostComponent>(ent) ||
            !(HasComp<RefillableSolutionComponent>(ent) || HasComp<DrainableSolutionComponent>(ent) ||
              HasComp<ChemicalFuelGeneratorAdapterComponent>(ent)))
            return;
        var fuel = _solutions.EnumerateSolutions((ent.Owner, ent.Comp)).Sum(s => UsableLiquid(s.Solution.Comp.Solution));
        if (fuel > 0 || HasComp<ChemicalFuelGeneratorAdapterComponent>(ent))
            Gauge(fuel, "fuel-module-liquid-units", ref args);
    }
    private void OnMaterials(Entity<MaterialStorageComponent> ent, ref ExaminedEvent args)
    {
        if (args.IsInDetailsRange && TryComp<SolidFuelGeneratorAdapterComponent>(ent, out var adapter))
            Gauge(ent.Comp.Storage.Values.Sum() + adapter.FractionalMaterial, "fuel-module-solid-units", ref args);
    }
    private void Gauge(float amount, string units, ref ExaminedEvent args) =>
        args.PushMarkup(Loc.GetString("fuel-gauge-reading", ("amount", Math.Round(amount, 2)), ("units", Loc.GetString(units))));
}
