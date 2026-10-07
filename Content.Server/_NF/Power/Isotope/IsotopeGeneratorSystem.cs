// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using Content.Shared._NF.Power.Isotope;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Construction.Components;
using Content.Shared.Examine;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Power.Components;
using Content.Shared.Audio;
using Content.Shared.Interaction;
using Content.Shared.Tools.Systems;
using Content.Shared.Verbs;
using Content.Shared.Wires;
using Robust.Shared.Utility;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Server._NF.Power.Isotope;

public sealed class IsotopeGeneratorSystem : EntitySystem
{
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;
    [Dependency] private readonly IsotopeCellSystem _generator = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly AtmosphereSystem _atmosphere = default!;
    [Dependency] private readonly SharedAmbientSoundSystem _audio = default!;

    [Dependency] private readonly SharedToolSystem _tools = default!;
    [Dependency] private readonly ExamineSystemShared _examine = default!;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesAfter.Add(typeof(IsotopeCellSystem));
        SubscribeLocalEvent<IsotopeGeneratorComponent, EntInsertedIntoContainerMessage>(OnContainerInserted);
        SubscribeLocalEvent<IsotopeGeneratorComponent, EntRemovedFromContainerMessage>(OnContainerRemoved);
        SubscribeLocalEvent<IsotopeGeneratorComponent, RefreshPartsEvent>(OnRefreshParts);
        SubscribeLocalEvent<IsotopeGeneratorComponent, UpgradeExamineEvent>(OnUpgradeExamine);
        SubscribeLocalEvent<IsotopeGeneratorComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<IsotopeGeneratorComponent, InteractUsingEvent>(OnInteractUsing,
            before: new[] { typeof(Content.Server.Construction.ConstructionSystem) });
        SubscribeLocalEvent<IsotopeGeneratorComponent, GetVerbsEvent<ExamineVerb>>(OnGetExamineVerbs);
    }

    private void OnContainerInserted(Entity<IsotopeGeneratorComponent> entity, ref EntInsertedIntoContainerMessage arguments) => UpdateGenerator(entity, 0f);
    private void OnContainerRemoved(Entity<IsotopeGeneratorComponent> entity, ref EntRemovedFromContainerMessage arguments) => UpdateGenerator(entity, 0f);

    private void OnRefreshParts(Entity<IsotopeGeneratorComponent> entity, ref RefreshPartsEvent arguments)
    {
        entity.Comp.CapacitorRating = Math.Clamp(arguments.PartRatings.GetValueOrDefault("Capacitor", 1f), 1f, 4f);
        UpdateGenerator(entity, 0f);
    }

    private void OnUpgradeExamine(Entity<IsotopeGeneratorComponent> entity, ref UpgradeExamineEvent arguments)
    {
        arguments.AddPercentageUpgrade("isotope-conversion", 1f + 0.05f * (entity.Comp.CapacitorRating - 1f));
    }

    public override void Update(float frameTime)
    {
        var arguments = EntityQueryEnumerator<IsotopeGeneratorComponent>();
        while (arguments.MoveNext(out var entity, out var generator))
            UpdateGenerator((entity, generator), frameTime);
    }

    public void UpdateGenerator(Entity<IsotopeGeneratorComponent> entity, float frameTime)
    {
        if (!TryComp<PowerSupplierComponent>(entity, out var supplier))
            return;
        var electricalOutput = 0f;
        var heatOutput = 0f;
        foreach (var slotId in entity.Comp.CellSlots)
        {
            if (_itemSlots.GetItemOrNull(entity, slotId) is not { } cell || !TryComp<IsotopeCellComponent>(cell, out var cellComponent))
                continue;
            _generator.ActivateCell((cell, cellComponent));
            if (cellComponent.FuelType is not { } fuel)
                continue;
            var fuelPrototype = _prototypeManager.Index(fuel);
            var outputFraction = _generator.GetOutputFraction((cell, cellComponent));
            electricalOutput += fuelPrototype.ElectricalOutput * outputFraction * cellComponent.CurrentVariation;
            heatOutput += fuelPrototype.HeatOutput * outputFraction;
        }
        electricalOutput *= 1f + 0.05f * (entity.Comp.CapacitorRating - 1f);
        supplier.MaxSupply = Transform(entity).Anchored ? electricalOutput : 0f;
        supplier.Enabled = supplier.MaxSupply > 0f;
        _audio.SetAmbience(entity.Owner, electricalOutput > 0f);
        if (heatOutput > 0f && frameTime > 0f && _atmosphere.GetContainingMixture(entity.Owner, false, true) is { TotalMoles: > 0f } atmosphere)
            _atmosphere.AddHeat(atmosphere, heatOutput * frameTime);
    }

    private void OnExamined(Entity<IsotopeGeneratorComponent> entity, ref ExaminedEvent arguments)
    {
        if (!arguments.IsInDetailsRange)
            return;

        var heatOutput = 0f;
        foreach (var slotId in entity.Comp.CellSlots)
        {
            if (_itemSlots.GetItemOrNull(entity, slotId) is { } cell && TryComp<IsotopeCellComponent>(cell, out var cellComponent))
                heatOutput += _generator.GetOutputFraction((cell, cellComponent));
        }
        arguments.PushMarkup(Loc.GetString(heatOutput > 0f ? "isotope-warm" : "isotope-quiet"));

        if (!TryComp<WiresPanelComponent>(entity, out var panel) || !panel.Open)
            return;

        foreach (var slotId in entity.Comp.CellSlots)
        {
            if (!_itemSlots.TryGetSlot(entity, slotId, out var slot))
                continue;
            if (_itemSlots.GetItemOrNull(entity, slotId) is { } cell && TryComp<IsotopeCellComponent>(cell, out var cellComponent))
            {
                var markup = Loc.GetString("isotope-slot-filled", ("slot", Loc.GetString(slot.Name)), ("cell", Name(cell)));
                if (cellComponent.State == IsotopeCellState.Active)
                    markup += " " + Loc.GetString("isotope-cell-gauge",
                        ("minutes", Math.Ceiling(cellComponent.RemainingLifetime / 60f)));
                arguments.PushMarkup(markup);
            }
            else
                arguments.PushMarkup(Loc.GetString("isotope-slot-empty", ("slot", Loc.GetString(slot.Name))));
        }
    }

    public string GetDiagnosticReadout(EntityUid entity)
    {
        return Loc.GetString("isotope-output",
            ("power", TryComp<PowerSupplierComponent>(entity, out var supplier) ? supplier.MaxSupply / 1000f : 0f));
    }

    private void OnInteractUsing(Entity<IsotopeGeneratorComponent> entity, ref InteractUsingEvent arguments)
    {
        if (arguments.Handled || !_tools.HasQuality(arguments.Used, SharedToolSystem.PulseQuality))
            return;
        var markup = FormattedMessage.FromMarkupOrThrow(GetDiagnosticReadout(entity));
        _examine.SendExamineTooltip(arguments.User, entity, markup, false, false);
        arguments.Handled = true;
    }

    private void OnGetExamineVerbs(Entity<IsotopeGeneratorComponent> entity, ref GetVerbsEvent<ExamineVerb> arguments)
    {
        if (!arguments.CanAccess || !arguments.CanInteract || !_examine.IsInDetailsRange(arguments.User, entity))
            return;
        var user = arguments.User;
        var verb = new ExamineVerb
        {
            Disabled = arguments.Using is not { } part || !_tools.HasQuality(part, SharedToolSystem.PulseQuality),
            Text = Loc.GetString("isotope-diagnostics-verb"),
            Message = Loc.GetString("isotope-diagnostics-hint"),
            Category = VerbCategory.Examine,
            Icon = new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/zap.svg.192dpi.png")),
            Act = () =>
            {
                var markup = FormattedMessage.FromMarkupOrThrow(GetDiagnosticReadout(entity));
                _examine.SendExamineTooltip(user, entity, markup, false, false);
            },
        };
        arguments.Verbs.Add(verb);
    }
}

