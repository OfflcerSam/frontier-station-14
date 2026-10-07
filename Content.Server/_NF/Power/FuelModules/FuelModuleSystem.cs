// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
using Content.Server._NF.Construction.Components;
using Content.Server.Materials;
using Content.Server.Fluids.EntitySystems;
using Content.Server.Power.Generator;
using Content.Server.Storage.EntitySystems;
using Content.Server.Popups;
using Content.Shared._NF.Power.FuelModules;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Construction.Components;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction;
using Content.Shared.Lock;
using Content.Shared.Materials;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Power.Generator;
using Content.Shared.Stacks;
using Content.Shared.Storage;
using Content.Shared.Throwing;
using Content.Shared.Tools.Systems;
using Content.Shared.Verbs;
using Content.Shared.Wires;
using Robust.Shared.Containers;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;
using Robust.Shared.Map.Components;

namespace Content.Server._NF.Power.FuelModules;

/// <summary>Exclusive fuel routing to removable, size-matched containers.</summary>
public sealed class FuelModuleSystem : EntitySystem
{
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly Content.Server.Stack.StackSystem _stack = default!;
    [Dependency] private readonly MaterialStorageSystem _materialStorage = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutionContainers = default!;
    [Dependency] private readonly GeneratorSystem _generator = default!;
    [Dependency] private readonly OpenableSystem _openable = default!;
    [Dependency] private readonly SharedToolSystem _tools = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly PuddleSystem _puddles = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MapGridComponent, StartCollideEvent>(OnCollision);
        SubscribeLocalEvent<FuelModuleComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<FuelModuleComponent, InteractUsingEvent>(OnInteractUsing, before: new[] { typeof(Content.Server.Construction.ConstructionSystem) });
        SubscribeLocalEvent<FuelModuleComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<FuelModuleComponent, StartCollideEvent>(OnCollision);
        SubscribeLocalEvent<FuelModuleComponent, EntInsertedIntoContainerMessage>(OnContainerInserted);
        SubscribeLocalEvent<FuelModuleComponent, EntRemovedFromContainerMessage>(OnContainerRemoved);
        SubscribeLocalEvent<FuelModuleHostComponent, InteractUsingEvent>(OnInteractUsing,
            before: new[] { typeof(Content.Server.Construction.ConstructionSystem) });
        SubscribeLocalEvent<FuelModuleHostComponent, GetVerbsEvent<InteractionVerb>>(OnGetVerbs);
        SubscribeLocalEvent<FuelModuleHostComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<FuelModuleHostComponent, StartCollideEvent>(OnCollision);
        SubscribeLocalEvent<FuelModuleHostComponent, GeneratorGetFuelEvent>(OnGetFuel);
        SubscribeLocalEvent<FuelModuleHostComponent, GeneratorUseFuel>(OnUseFuel);
        SubscribeLocalEvent<FuelModuleHostComponent, GeneratorEmpty>(OnEmptyFuel);
        SubscribeLocalEvent<FuelModuleHostComponent, GeneratorGetCloggedEvent>(OnGetClogged);
        SubscribeLocalEvent<FuelModuleHostComponent, EntRemovedFromContainerMessage>(OnContainerRemoved);
    }

    private void OnMapInit(Entity<FuelModuleComponent> entity, ref MapInitEvent arguments) => UpdateModuleCapacity(entity);
    private void OnContainerInserted(Entity<FuelModuleComponent> entity, ref EntInsertedIntoContainerMessage arguments) => UpdateModuleCapacity(entity);
    private void OnContainerRemoved(Entity<FuelModuleComponent> entity, ref EntRemovedFromContainerMessage arguments) => UpdateModuleCapacity(entity);
    private void OnContainerRemoved(Entity<FuelModuleHostComponent> entity, ref EntRemovedFromContainerMessage arguments)
    {
        if (arguments.Container.ID == entity.Comp.ModuleSlot)
            _generator.SetFuelGeneratorOn(entity, false);
    }

    public EntityUid? GetInstalledModule(EntityUid host)
    {
        return TryComp<FuelModuleHostComponent>(host, out var hostComponent)
            ? _itemSlots.GetItemOrNull(host, hostComponent.ModuleSlot) : null;
    }

    public bool TryInstallModule(EntityUid host, EntityUid module, EntityUid user)
    {
        if (!TryComp<FuelModuleHostComponent>(host, out var hostComponent) ||
            !TryComp<FuelModuleComponent>(module, out var moduleComponent) ||
            moduleComponent.ModuleSize != hostComponent.ModuleSize ||
            !CanService(host) || GetInstalledModule(host) != null ||
            !_itemSlots.TryGetSlot(host, hostComponent.ModuleSlot, out var moduleSlot))
            return false;
        _itemSlots.SetLock(host, hostComponent.ModuleSlot, false);
        try
        {
            return _itemSlots.TryInsert(host, moduleSlot, module, user);
        }
        finally { _itemSlots.SetLock(host, hostComponent.ModuleSlot, true); }
    }

    public bool TryRemoveModule(EntityUid host, EntityUid user)
    {
        if (!CanService(host) || !TryComp<FuelModuleHostComponent>(host, out var hostComponent))
            return false;
        _itemSlots.SetLock(host, hostComponent.ModuleSlot, false);
        try { return _itemSlots.TryEject(host, hostComponent.ModuleSlot, user, out _); }
        finally { _itemSlots.SetLock(host, hostComponent.ModuleSlot, true); }
    }

    private bool CanService(EntityUid host) =>
        TryComp<WiresPanelComponent>(host, out var panel) && panel.Open &&
        TryComp<FuelGeneratorComponent>(host, out var generator) && !generator.On;

    private void OnInteractUsing(Entity<FuelModuleHostComponent> entity, ref InteractUsingEvent arguments)
    {
        if (arguments.Handled)
            return;
        if (HasComp<FuelModuleComponent>(arguments.Used))
        {
            arguments.Handled = true;
            if (!TryInstallModule(entity, arguments.Used, arguments.User))
                _popup.PopupEntity(Loc.GetString("fuel-module-install-denied"), entity, arguments.User);
            return;
        }
        if (_tools.HasQuality(arguments.Used, "Prying") && GetInstalledModule(entity) != null)
        {
            arguments.Handled = true;
            if (!TryRemoveModule(entity, arguments.User))
                _popup.PopupEntity(Loc.GetString("fuel-module-service-denied"), entity, arguments.User);
            return;
        }
        if (GetInstalledModule(entity) is not { } module)
            return;
        if (TryLoadSolidFuel((module, Comp<FuelModuleComponent>(module)), arguments.Used, arguments.User))
            arguments.Handled = true;
        else if (Comp<FuelModuleComponent>(module).Kind == FuelModuleKind.Liquid &&
                 !_openable.IsClosed(module) &&
                 _solutionContainers.TryGetDrainableSolution(arguments.Used, out var fuelSolution, out var solution) &&
                 _solutionContainers.TryGetSolution((EntityUid) module, "tank", out var fuelCapacity, out var fuelVolume))
        {
            // Use the existing pouring interaction on the actual tank, including transfer amount and source lid checks.
            var startAttempt = new AfterInteractEvent(arguments.User, arguments.Used, module, arguments.ClickLocation, true);
            RaiseLocalEvent(arguments.Used, startAttempt);
            arguments.Handled = startAttempt.Handled;
        }
    }

    private void OnInteractUsing(Entity<FuelModuleComponent> entity, ref InteractUsingEvent arguments)
    {
        if (!arguments.Handled && entity.Comp.Kind == FuelModuleKind.Solid)
            arguments.Handled = TryLoadSolidFuel(entity, arguments.Used, arguments.User);
    }

    public bool TryLoadSolidFuel(Entity<FuelModuleComponent> module, EntityUid material, EntityUid user)
    {
        if (module.Comp.Kind != FuelModuleKind.Solid || _openable.IsClosed(module) ||
            !TryComp<StackComponent>(material, out var quantity) ||
            !TryComp<PhysicalCompositionComponent>(material, out var composition) ||
            composition.MaterialComposition.Count != 1)
            return false;
        var materialId = composition.MaterialComposition.Keys.First();
        if (!module.Comp.MaterialFuelValues.ContainsKey(materialId))
            return false;
        var materialUnits = composition.MaterialComposition[materialId];
        var availableVolume = module.Comp.BaseCapacity * (1f + 0.2f * (module.Comp.MatterBinRating - 1f)) - module.Comp.FractionalFuel.Values.Sum();
        var acceptedCount = Math.Min(quantity.Count, (int) (availableVolume / materialUnits));
        if (acceptedCount <= 0 || !_stack.Use(material, acceptedCount, quantity))
            return false;
        module.Comp.FractionalFuel[materialId] = module.Comp.FractionalFuel.GetValueOrDefault(materialId) + acceptedCount * materialUnits;
        return true;
    }

    public float GetModuleFuel(Entity<FuelModuleComponent> module)
    {
        if (module.Comp.Kind == FuelModuleKind.Liquid)
            return _generator.GetFuel(module);
        var availableFuel = 0f;
        foreach (var (materialId, fuelAmount) in module.Comp.FractionalFuel)
            availableFuel += fuelAmount * module.Comp.MaterialFuelValues.GetValueOrDefault(materialId);
        return availableFuel;
    }

    public void ConsumeModuleFuel(Entity<FuelModuleComponent> module, float fuelAmount)
    {
        if (!float.IsFinite(fuelAmount) || fuelAmount <= 0f)
            return;
        if (module.Comp.Kind == FuelModuleKind.Liquid)
        {
            RaiseLocalEvent(module, new GeneratorUseFuel(fuelAmount));
            return;
        }
        var availableFuel = GetModuleFuel(module);
        if (availableFuel <= 0f)
            return;
        var burnMultiplier = 1f - Math.Clamp(fuelAmount / availableFuel, 0f, 1f);
        foreach (var materialId in module.Comp.FractionalFuel.Keys.ToArray())
            module.Comp.FractionalFuel[materialId] *= burnMultiplier;
    }

    private void OnGetFuel(Entity<FuelModuleHostComponent> entity, ref GeneratorGetFuelEvent arguments)
    {
        arguments.Fuel = GetInstalledModule(entity) is { } module
            ? GetModuleFuel((module, Comp<FuelModuleComponent>(module))) : 0f;
    }

    private void OnUseFuel(Entity<FuelModuleHostComponent> entity, ref GeneratorUseFuel arguments)
    {
        if (GetInstalledModule(entity) is { } module)
            ConsumeModuleFuel((module, Comp<FuelModuleComponent>(module)), arguments.FuelUsed);
    }

    private void OnGetClogged(Entity<FuelModuleHostComponent> entity, ref GeneratorGetCloggedEvent arguments)
    {
        if (GetInstalledModule(entity) is { } module && Comp<FuelModuleComponent>(module).Kind == FuelModuleKind.Liquid)
            arguments.Clogged = _generator.GetIsClogged(module);
    }

    private void OnEmptyFuel(Entity<FuelModuleHostComponent> entity, ref GeneratorEmpty arguments)
    {
        if (CanService(entity) && GetInstalledModule(entity) is { } module)
            EmptyModule((module, Comp<FuelModuleComponent>(module)));
    }

    public void EmptyModule(Entity<FuelModuleComponent> module)
    {
        if (!_openable.IsClosed(module))
            SpillContents(module, 1f);
    }

    public void UpdateModuleCapacity(Entity<FuelModuleComponent> module)
    {
        var part = _itemSlots.GetItemOrNull(module, "matter_bin");
        module.Comp.MatterBinRating = part != null && TryComp<MachinePartComponent>(part, out var state)
            ? Math.Clamp(state.Rating, 1f, 4f) : 1f;
        if (module.Comp.Kind == FuelModuleKind.Liquid &&
            _solutionContainers.TryGetSolution((EntityUid) module, "tank", out var fuelSolution, out var solution))
        {
            var targetCapacity = FixedPoint2.New(module.Comp.BaseCapacity * (1f + 0.2f * (module.Comp.MatterBinRating - 1f)));
            // Upgrade servicing never shrinks a filled container or deletes fuel.
            _solutionContainers.SetCapacity(fuelSolution.Value, FixedPoint2.Max(targetCapacity, solution.Volume));
        }
    }

    public bool TryExchangeModulePart(Entity<FuelModuleComponent> module, EntityUid exchanger)
    {
        if (_openable.IsClosed(module) || !TryComp<StorageComponent>(exchanger, out var arguments) ||
            arguments.Container == null || !TryComp<PartExchangerComponent>(exchanger, out var generator) ||
            !generator.PreferHigherRating)
            return false;
        EntityUid? replacementPart = null;
        var previousRating = module.Comp.MatterBinRating;
        foreach (var part in arguments.Container.ContainedEntities)
        {
            if (TryComp<MachinePartComponent>(part, out var state) && state.PartType == "MatterBin" && state.Rating > previousRating)
            {
                replacementPart = part;
                previousRating = state.Rating;
            }
        }
        if (replacementPart == null)
            return false;
        if (TryComp<StackComponent>(replacementPart, out var quantity) && quantity.Count > 1)
            replacementPart = _stack.Split(replacementPart.Value, 1, Transform(module).Coordinates, quantity);
        if (replacementPart == null)
            return false;
        var moduleSlot = _itemSlots.GetItemOrNull(module, "matter_bin");
        _itemSlots.SetLock(module, "matter_bin", false);
        try
        {
            if (moduleSlot != null && !_itemSlots.TryEject(module, "matter_bin", null, out _))
                return false;
            if (!_itemSlots.TryInsert(module, "matter_bin", replacementPart.Value, null))
            {
                if (moduleSlot != null)
                    _itemSlots.TryInsert(module, "matter_bin", moduleSlot.Value, null);
                return false;
            }
            if (moduleSlot != null)
                EntityManager.System<StorageSystem>().Insert(exchanger, moduleSlot.Value, out _);
            UpdateModuleCapacity(module);
            return true;
        }
        finally { _itemSlots.SetLock(module, "matter_bin", true); }
    }

    public bool ToggleLid(EntityUid module, EntityUid user)
    {
        return TryComp<OpenableComponent>(module, out var arguments) && _openable.TryToggle((module, arguments), user);
    }

    public bool ToggleLidLock(EntityUid module, EntityUid user)
    {
        return EntityManager.System<LockSystem>().IsLocked((EntityUid) module)
            ? EntityManager.System<LockSystem>().TryUnlock(module, user)
            : EntityManager.System<LockSystem>().TryLock(module, user);
    }

    private void OnGetVerbs(Entity<FuelModuleHostComponent> entity, ref GetVerbsEvent<InteractionVerb> arguments)
    {
        if (!arguments.CanAccess || !arguments.CanInteract || arguments.Hands == null || GetInstalledModule(entity) is not { } module)
            return;
        var user = arguments.User;
        arguments.Verbs.Add(new InteractionVerb
        {
            Text = Loc.GetString(_openable.IsClosed(module) ? "fuel-module-open" : "fuel-module-close"),
            Act = () => ToggleLid(module, user),
        });
        arguments.Verbs.Add(new InteractionVerb
        {
            Text = Loc.GetString(EntityManager.System<LockSystem>().IsLocked((EntityUid) module) ? "fuel-module-unlock" : "fuel-module-lock"),
            Act = () => ToggleLidLock(module, user),
        });
    }

    private void OnExamined(Entity<FuelModuleHostComponent> entity, ref ExaminedEvent arguments)
    {
        if (arguments.IsInDetailsRange && TryComp<WiresPanelComponent>(entity, out var panel) && panel.Open)
            arguments.PushMarkup(Loc.GetString("fuel-module-installed",
                ("module", GetInstalledModule(entity) is { } module ? Name(module) : Loc.GetString("fuel-module-none"))));
    }

    private void OnExamined(Entity<FuelModuleComponent> entity, ref ExaminedEvent arguments)
    {
        if (!arguments.IsInDetailsRange || _openable.IsClosed(entity))
            return;
        arguments.PushMarkup(Loc.GetString("fuel-module-capacity", ("capacity",
            entity.Comp.BaseCapacity * (1f + 0.2f * (entity.Comp.MatterBinRating - 1f))),
            ("units", Loc.GetString(entity.Comp.Kind == FuelModuleKind.Liquid ? "fuel-module-liquid-units" : "fuel-module-solid-units"))));
        if (entity.Comp.Kind == FuelModuleKind.Solid)
            foreach (var (materialId, materialUnits) in entity.Comp.FractionalFuel)
                if (materialUnits > 0f)
                    arguments.PushText(Loc.GetString("fuel-module-material", ("material", materialId), ("amount", Math.Floor(materialUnits / 100f))));
    }

    private void OnCollision(Entity<FuelModuleHostComponent> entity, ref StartCollideEvent arguments)
    {
        if (GetInstalledModule(entity) is { } module)
            OnCollision((module, Comp<FuelModuleComponent>(module)), ref arguments);
    }

    private void OnCollision(Entity<FuelModuleComponent> entity, ref StartCollideEvent arguments)
    {
        // Catch fuel before evaluating impact, so successful loading never spills the hopper.
        if (HasComp<ThrownItemComponent>(arguments.OtherEntity) &&
            TryLoadSolidFuel(entity, arguments.OtherEntity, arguments.OtherEntity))
            return;
        var impactSpeed = (_physics.GetMapLinearVelocity(arguments.OurEntity) -
                           _physics.GetMapLinearVelocity(arguments.OtherEntity)).Length();
        HandleImpact(entity, impactSpeed);
    }

    private void OnCollision(Entity<MapGridComponent> entity, ref StartCollideEvent arguments)
    {
        var impactSpeed = (_physics.GetMapLinearVelocity(arguments.OurEntity) -
                           _physics.GetMapLinearVelocity(arguments.OtherEntity)).Length();
        if (impactSpeed < 15f || !HasComp<MapGridComponent>(arguments.OtherEntity))
            return;
        var moduleSlot = EntityQueryEnumerator<FuelModuleComponent, TransformComponent>();
        while (moduleSlot.MoveNext(out var module, out var moduleComponent, out var transform))
            if (transform.GridUid == entity.Owner)
                HandleImpact((module, moduleComponent), impactSpeed);
    }

    public bool HandleImpact(Entity<FuelModuleComponent> module, float impactSpeed)
    {
        if (!float.IsFinite(impactSpeed) || impactSpeed < module.Comp.MinimumImpactSpeed ||
            _timing.CurTime < module.Comp.NextSpillTime || EntityManager.System<LockSystem>().IsLocked((EntityUid) module))
            return false;
        if (_openable.IsClosed(module) && !_openable.TryOpen(module))
            return false;
        module.Comp.NextSpillTime = _timing.CurTime + TimeSpan.FromSeconds(2);
        SpillContents(module, module.Comp.SpillFraction);
        return true;
    }

    public void SpillContents(Entity<FuelModuleComponent> module, float spillAmount)
    {
        spillAmount = Math.Clamp(spillAmount, 0f, 1f);
        if (module.Comp.Kind == FuelModuleKind.Liquid)
        {
            if (_solutionContainers.TryGetSolution((EntityUid) module, "tank", out var fuelSolution, out var solution))
            {
                var fuelVolume = _solutionContainers.SplitSolution(fuelSolution.Value, solution.Volume * spillAmount);
                _puddles.TrySpillAt(Transform(module).Coordinates, fuelVolume, out _);
            }
            return;
        }
        foreach (var materialId in module.Comp.FractionalFuel.Keys.ToArray())
        {
            var materialUnits = (int) Math.Floor(module.Comp.FractionalFuel[materialId] * spillAmount);
            _materialStorage.SpawnMultipleFromMaterial(materialUnits, materialId, Transform(module).Coordinates, out var remainingUnits);
            module.Comp.FractionalFuel[materialId] -= materialUnits - remainingUnits;
        }
    }
}




