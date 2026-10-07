// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
using Content.Shared.Containers.ItemSlots;
using Content.Shared._NF.Power.Isotope;
using Content.Shared.Whitelist;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Tools.Systems;
using Content.Shared.Wires;
using Content.Server.Popups;
using Content.Server.Radiation.Components;
using Robust.Shared.Containers;

namespace Content.Server._NF.Power.Isotope;

/// <summary>Reusable radiation shielding with tool-serviced slots. No fuel-specific behavior.</summary>
public sealed class RadiationShieldingSystem : EntitySystem
{
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;
    [Dependency] private readonly SharedToolSystem _tools = default!;
    [Dependency] private readonly PopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RadiationShieldingComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<RadiationShieldingComponent, EntInsertedIntoContainerMessage>(OnContainerInserted);
        SubscribeLocalEvent<RadiationShieldingComponent, EntRemovedFromContainerMessage>(OnContainerRemoved);
        SubscribeLocalEvent<RadiationShieldingComponent, InteractUsingEvent>(OnInteractUsing, before: new[] { typeof(ItemSlotsSystem), typeof(Content.Server.Construction.ConstructionSystem) });
        SubscribeLocalEvent<RadiationShieldingComponent, ExaminedEvent>(OnExamined);
    }

    private void OnMapInit(Entity<RadiationShieldingComponent> entity, ref MapInitEvent arguments) => UpdateShielding(entity);
    private void OnContainerInserted(Entity<RadiationShieldingComponent> entity, ref EntInsertedIntoContainerMessage arguments) => UpdateShielding(entity);
    private void OnContainerRemoved(Entity<RadiationShieldingComponent> entity, ref EntRemovedFromContainerMessage arguments) => UpdateShielding(entity);

    public void UpdateShielding(Entity<RadiationShieldingComponent> entity)
    {
        if (!TryComp<RadiationBlockingContainerComponent>(entity, out var shielding))
            return;
        shielding.RadResistance = entity.Comp.BaselineResistance;
        if (_itemSlots.GetItemOrNull(entity, entity.Comp.ShieldingSlot) is { } insert &&
            TryComp<RadiationShieldingInsertComponent>(insert, out var part))
            shielding.RadResistance = Math.Max(entity.Comp.BaselineResistance, part.Resistance);
    }

    public bool CanService(EntityUid entity) => TryComp<WiresPanelComponent>(entity, out var panel) && panel.Open;

    private void OnInteractUsing(Entity<RadiationShieldingComponent> entity, ref InteractUsingEvent arguments)
    {
        if (arguments.Handled || !TryComp<ItemSlotsComponent>(entity, out var slot))
            return;
        if (_tools.HasQuality(arguments.Used, "Prying"))
        {
            arguments.Handled = true;
            if (!CanService(entity))
                _popup.PopupEntity(Loc.GetString("isotope-open-hatch"), entity, arguments.User);
            else
                arguments.Handled = TryRemoveContents(entity, arguments.User);
            return;
        }

        // Slot insertion/ejection stays locked outside this synchronous service operation.
        foreach (var slotId in new[] { entity.Comp.ShieldingSlot }.Concat(entity.Comp.CellSlots))
        {
            if (!_itemSlots.TryGetSlot(entity, slotId, out var insert, slot) || insert.HasItem ||
                EntityManager.System<EntityWhitelistSystem>().IsWhitelistFail(insert.Whitelist, arguments.Used))
                continue;
            arguments.Handled = true;
            if (!CanService(entity))
            {
                _popup.PopupEntity(Loc.GetString("isotope-open-hatch"), entity, arguments.User);
                return;
            }
            if (slotId != entity.Comp.ShieldingSlot && _itemSlots.GetItemOrNull(entity, entity.Comp.ShieldingSlot) != null)
            {
                _popup.PopupEntity(Loc.GetString("isotope-remove-shielding"), entity, arguments.User);
                return;
            }
            _itemSlots.SetLock(entity, slotId, false);
            try
            {
                _itemSlots.TryInsertFromHand(entity, insert, arguments.User);
            }
            finally
            {
                _itemSlots.SetLock(entity, slotId, true);
            }
            return;
        }
    }

    public bool TryRemoveContents(Entity<RadiationShieldingComponent> entity, EntityUid user)
    {
        if (!CanService(entity))
            return false;
        foreach (var slotId in new[] { entity.Comp.ShieldingSlot }.Concat(entity.Comp.CellSlots))
        {
            if (_itemSlots.GetItemOrNull(entity, slotId) == null)
                continue;
            _itemSlots.SetLock(entity, slotId, false);
            try
            {
                return _itemSlots.TryEject(entity, slotId, user, out _);
            }
            finally
            {
                _itemSlots.SetLock(entity, slotId, true);
            }
        }
        return false;
    }

    private void OnExamined(Entity<RadiationShieldingComponent> entity, ref ExaminedEvent arguments)
    {
        if (!arguments.IsInDetailsRange || !CanService(entity))
            return;
        var shielding = _itemSlots.GetItemOrNull(entity, entity.Comp.ShieldingSlot);
        arguments.PushMarkup(Loc.GetString("isotope-shielding", ("rating",
            shielding != null && TryComp<RadiationShieldingInsertComponent>(shielding, out var insert) ? insert.Rating : 1)));

    }
}




