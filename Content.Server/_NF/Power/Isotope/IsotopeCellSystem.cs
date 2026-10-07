// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using Content.Shared._NF.Power.Isotope;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Materials;
using Content.Shared.Radiation.Components;
using Content.Shared.Stacks;
using Content.Server.Popups;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.Server._NF.Power.Isotope;

/// <summary>Finite sealed fuel. Removing a cell never resets or pauses its simulation lifetime.</summary>
public sealed class IsotopeCellSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly SharedStackSystem _stack = default!;
    [Dependency] private readonly MetaDataSystem _metadata = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly IRobustRandom _random = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<IsotopeCellComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<IsotopeCellComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<IsotopeCellComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<IsotopeCellComponent, MaterialReclaimAttemptEvent>(OnReclaimAttempt);
    }

    private void OnMapInit(Entity<IsotopeCellComponent> entity, ref MapInitEvent arguments)
    {
        UpdateCellAppearance(entity);
        UpdateCell(entity, 0f);
    }

    private void OnInteractUsing(Entity<IsotopeCellComponent> entity, ref InteractUsingEvent arguments)
    {
        if (arguments.Handled)
            return;
        if (TryLoadFuel(entity, arguments.Used))
        {
            arguments.Handled = true;
            _popup.PopupEntity(Loc.GetString("isotope-cell-loading", ("amount", entity.Comp.LoadedMaterial),
                ("required", _prototypeManager.Index(entity.Comp.FuelType!.Value).RequiredMaterial)), entity, arguments.User);
        }
        else
            _popup.PopupEntity(Loc.GetString("isotope-cell-loading-rejected"), entity, arguments.User);
    }

    public bool TryLoadFuel(Entity<IsotopeCellComponent> entity, EntityUid material)
    {
        if (entity.Comp.State is IsotopeCellState.Loaded or IsotopeCellState.Active ||
            !TryComp<StackComponent>(material, out var quantity) ||
            !TryComp<PhysicalCompositionComponent>(material, out var composition))
            return false;

        foreach (var fuelPrototype in _prototypeManager.EnumeratePrototypes<IsotopeFuelPrototype>())
        {
            if (entity.Comp.State == IsotopeCellState.Loading && entity.Comp.FuelType != fuelPrototype.ID)
                continue;
            var materialUnits = 0;
            foreach (var materialId in fuelPrototype.AcceptedMaterials)
                materialUnits += composition.MaterialComposition.GetValueOrDefault(materialId);
            if (materialUnits <= 0)
                continue;

            var remainingUnits = fuelPrototype.RequiredMaterial - (entity.Comp.State == IsotopeCellState.Loading ? entity.Comp.LoadedMaterial : 0);
            var acceptedCount = Math.Min(quantity.Count, remainingUnits / materialUnits);
            // Avoid an unfillable 50-unit remainder when mixing 100-unit rods and 150-unit pieces.
            if (remainingUnits - acceptedCount * materialUnits == 50)
                acceptedCount--;
            if (acceptedCount <= 0 || !_stack.Use(material, acceptedCount, quantity))
                return false;

            if (entity.Comp.State != IsotopeCellState.Loading)
                entity.Comp.LoadedMaterial = 0;
            entity.Comp.FuelType = fuelPrototype.ID;
            entity.Comp.LoadedMaterial += acceptedCount * materialUnits;
            entity.Comp.State = entity.Comp.LoadedMaterial == fuelPrototype.RequiredMaterial
                ? IsotopeCellState.Loaded : IsotopeCellState.Loading;
            UpdateCellAppearance(entity);
            return true;
        }
        return false;
    }

    public void ActivateCell(Entity<IsotopeCellComponent> entity)
    {
        if (entity.Comp.State != IsotopeCellState.Loaded || entity.Comp.FuelType is not { } fuel)
            return;
        entity.Comp.State = IsotopeCellState.Active;
        entity.Comp.RemainingLifetime = _prototypeManager.Index(fuel).Lifetime;
        entity.Comp.NextVariation = 0f;
        UpdateCellAppearance(entity);
        UpdateCell(entity, 0f);
    }

    public void DepleteCell(Entity<IsotopeCellComponent> entity)
    {
        entity.Comp.State = IsotopeCellState.Depleted;
        entity.Comp.RemainingLifetime = 0f;
        entity.Comp.LoadedMaterial = 0;
        entity.Comp.CurrentVariation = 1f;
        if (entity.Comp.FuelType is { } fuel)
            entity.Comp.DepletedRadiation = _prototypeManager.Index(fuel).DepletedRadiation;
        UpdateCellAppearance(entity);
    }

    public float GetOutputFraction(Entity<IsotopeCellComponent> entity)
    {
        if (entity.Comp.State != IsotopeCellState.Active || entity.Comp.RemainingLifetime <= 0f || entity.Comp.FuelType is not { } fuel)
            return 0f;
        var fuelPrototype = _prototypeManager.Index(fuel);
        var elapsed = 1f - entity.Comp.RemainingLifetime / fuelPrototype.Lifetime;
        return elapsed <= fuelPrototype.TaperStart ? 1f :
            MathHelper.Lerp(1f, fuelPrototype.TaperEndOutput,
                Math.Clamp((elapsed - fuelPrototype.TaperStart) / (1f - fuelPrototype.TaperStart), 0f, 1f));
    }

    public override void Update(float frameTime)
    {
        var arguments = EntityQueryEnumerator<IsotopeCellComponent>();
        while (arguments.MoveNext(out var entity, out var cellComponent))
            UpdateCell((entity, cellComponent), frameTime);
    }

    public void UpdateCell(Entity<IsotopeCellComponent> entity, float frameTime)
    {
        if (entity.Comp.FuelType is not { } fuel)
            return;
        var fuelPrototype = _prototypeManager.Index(fuel);
        if (entity.Comp.State == IsotopeCellState.Active)
        {
            entity.Comp.RemainingLifetime = Math.Max(0f, entity.Comp.RemainingLifetime - frameTime);
            if (entity.Comp.RemainingLifetime <= 0f)
                DepleteCell(entity);
            else
            {
                entity.Comp.NextVariation -= frameTime;
                if (entity.Comp.NextVariation <= 0f)
                {
                    entity.Comp.NextVariation = fuelPrototype.VariationInterval;
                    entity.Comp.CurrentVariation = 1f + _random.NextFloat(-fuelPrototype.OutputVariation, fuelPrototype.OutputVariation);
                }
            }
        }
        if (TryComp<RadiationSourceComponent>(entity, out var radiation))
        {
            radiation.Intensity = entity.Comp.State == IsotopeCellState.Active
                ? Math.Max(fuelPrototype.DepletedRadiation, fuelPrototype.ActiveRadiation * GetOutputFraction(entity))
                : entity.Comp.DepletedRadiation;
            radiation.Slope = fuelPrototype.RadiationSlope;
            radiation.Enabled = radiation.Intensity > 0f;
        }
    }

    private void UpdateCellAppearance(Entity<IsotopeCellComponent> entity)
    {
        _metadata.SetEntityName(entity, Loc.GetString("isotope-cell-name-" + entity.Comp.State.ToString().ToLowerInvariant()));
    }

    private void OnExamined(Entity<IsotopeCellComponent> entity, ref ExaminedEvent arguments)
    {
        if (!arguments.IsInDetailsRange || entity.Comp.FuelType is not { } fuel)
            return;
        var fuelPrototype = _prototypeManager.Index(fuel);
        arguments.PushMarkup(Loc.GetString("isotope-cell-details", ("fuel", Loc.GetString(fuelPrototype.ID)),
            ("amount", entity.Comp.LoadedMaterial), ("required", fuelPrototype.RequiredMaterial),
            ("minutes", Math.Ceiling(entity.Comp.RemainingLifetime / 60f))));
        if (entity.Comp.State == IsotopeCellState.Active)
            arguments.PushMarkup(Loc.GetString("isotope-cell-gauge",
                ("minutes", Math.Ceiling(entity.Comp.RemainingLifetime / 60f))));
    }

    private void OnReclaimAttempt(Entity<IsotopeCellComponent> entity, ref MaterialReclaimAttemptEvent arguments)
    {
        if (entity.Comp.State != IsotopeCellState.Depleted)
            arguments.Cancelled = true;
    }
}



