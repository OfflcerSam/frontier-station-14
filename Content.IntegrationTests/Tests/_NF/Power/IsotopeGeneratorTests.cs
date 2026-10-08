// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Numerics;
using System.Linq;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Verbs;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._NF.Power.Isotope;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Materials;
using Content.Server.Power.Components;
using Content.Server.Radiation.Components;
using Content.Shared._NF.Power.Isotope;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Materials;
using Content.Shared.Radiation.Components;
using Content.Shared.Stacks;
using Content.Shared.Wires;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._NF.Power;

public sealed class IsotopeGeneratorTests : InteractionTest
{
    [Test]
    public async Task CellLoadingAndDepletion()
    {
        await SpawnTarget("NFIsotopeCellCasing");
        await InteractUsing("UraniumOre1");
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.GetComponent<IsotopeCellComponent>(STarget!.Value).State, Is.EqualTo(IsotopeCellState.Empty)));
        await InteractUsing("SheetUranium", 20);
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.GetComponent<IsotopeCellComponent>(STarget!.Value).LoadedMaterial, Is.EqualTo(2000)));
        await InteractUsing("FuelBananium1");
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.GetComponent<IsotopeCellComponent>(STarget!.Value).LoadedMaterial, Is.EqualTo(2000)));
        await InteractUsing("FuelUranium", 15);
        await Server.WaitAssertion(() =>
        {
            var entity = STarget!.Value;
            var cell = SEntMan.GetComponent<IsotopeCellComponent>(entity);
            var generator = SEntMan.System<IsotopeCellSystem>();
            Assert.That(cell.State, Is.EqualTo(IsotopeCellState.Loaded));
            Assert.That(cell.LoadedMaterial, Is.EqualTo(3000));
            Assert.That(SEntMan.GetComponent<StackComponent>(HandSys.GetActiveItem((SPlayer, Hands))!.Value).Count, Is.EqualTo(5));
            Assert.That(SEntMan.GetComponent<RadiationSourceComponent>(entity).Enabled, Is.False);
            generator.ActivateCell((entity, cell));
            Assert.That(cell.RemainingLifetime, Is.EqualTo(14400f));
            generator.UpdateCell((entity, cell), 10800f);
            Assert.That(generator.GetOutputFraction((entity, cell)), Is.EqualTo(1f));
            generator.ActivateCell((entity, cell));
            Assert.That(cell.RemainingLifetime, Is.EqualTo(3600f));
            generator.UpdateCell((entity, cell), 1800f);
            Assert.That(generator.GetOutputFraction((entity, cell)), Is.EqualTo(0.7f).Within(0.001f));
            generator.UpdateCell((entity, cell), 1800f);
            Assert.That(cell.State, Is.EqualTo(IsotopeCellState.Depleted));
            Assert.That(SEntMan.GetComponent<RadiationSourceComponent>(entity).Intensity, Is.EqualTo(0.15f));
        });
        await InteractUsing("FuelBananium1");
        await InteractUsing("MaterialBananium", 10);
        await Server.WaitAssertion(() =>
        {
            // One rod plus eight pieces leaves 200 units; a ninth piece would strand 50 units.
            Assert.That(SEntMan.GetComponent<IsotopeCellComponent>(STarget!.Value).LoadedMaterial, Is.EqualTo(1300));
            Assert.That(SEntMan.GetComponent<StackComponent>(HandSys.GetActiveItem((SPlayer, Hands))!.Value).Count, Is.EqualTo(2));
            Assert.That(SEntMan.GetComponent<RadiationSourceComponent>(STarget.Value).Intensity, Is.EqualTo(0.15f));
        });
        await InteractUsing("FuelBananium", 2);
        await Server.WaitAssertion(() =>
        {
            var cell = SEntMan.GetComponent<IsotopeCellComponent>(STarget!.Value);
            Assert.That(cell.State, Is.EqualTo(IsotopeCellState.Loaded));
            SEntMan.System<IsotopeCellSystem>().ActivateCell((STarget.Value, cell));
            Assert.That(cell.RemainingLifetime, Is.EqualTo(9000f));
            Assert.That(cell.CurrentVariation, Is.InRange(0.8f, 1.2f));
            Assert.That(SEntMan.GetComponent<RadiationSourceComponent>(STarget.Value).Intensity, Is.EqualTo(1.25f));
        });
    }

    [TestCase("NFStationaryGeneratorIsotopeCompact")]
    [TestCase("NFStationaryGeneratorIsotopeStandard")]
    public async Task ShieldingRequiresTools(string prototypeId)
    {
        await SpawnTarget(prototypeId);
        await InteractUsing("NFRadiationShieldingInsertR4");
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.System<ItemSlotsSystem>().GetItemOrNull(STarget!.Value, "shielding"), Is.Null));
        await Interact(Screw, "NFRadiationShieldingInsertR4");
        await Server.WaitAssertion(() =>
        {
            var entity = STarget!.Value;
            Assert.That(SEntMan.GetComponent<WiresPanelComponent>(entity).Open, Is.True);
            var insert = SEntMan.System<ItemSlotsSystem>().GetItemOrNull(entity, "shielding");
            Assert.That(insert, Is.Not.Null);
            Assert.That(SEntMan.GetComponent<PhysicalCompositionComponent>(insert!.Value).MaterialComposition.Count, Is.EqualTo(2));
            Assert.That(SEntMan.GetComponent<RadiationBlockingContainerComponent>(entity).RadResistance, Is.EqualTo(1.25f));
            Assert.That(SEntMan.System<ItemSlotsSystem>().TryEject(entity, "shielding", SPlayer, out _), Is.False);
        });
        await InteractUsing("NFIsotopeCellCasing");
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.System<ItemSlotsSystem>().GetItemOrNull(STarget!.Value, "cell1"), Is.Null));
        await InteractUsing(Pry);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.System<ItemSlotsSystem>().GetItemOrNull(STarget!.Value, "shielding"), Is.Null);
            Assert.That(SEntMan.GetComponent<RadiationBlockingContainerComponent>(STarget.Value).RadResistance, Is.EqualTo(0.5f));
        });
        await InteractUsing("NFIsotopeCellCasing");
        await InteractUsing("NFRadiationShieldingInsertR2");
        await InteractUsing(Pry);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.System<ItemSlotsSystem>().GetItemOrNull(STarget!.Value, "shielding"), Is.Null);
            Assert.That(SEntMan.System<ItemSlotsSystem>().GetItemOrNull(STarget.Value, "cell1"), Is.Not.Null);
        });
        await InteractUsing(Screw);
        await InteractUsing(Pry);
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.System<ItemSlotsSystem>().GetItemOrNull(STarget!.Value, "cell1"), Is.Not.Null));
        await Interact(Screw, Pry);
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.System<ItemSlotsSystem>().GetItemOrNull(STarget!.Value, "cell1"), Is.Null));
        await InteractUsing("RPEDT4Filled");
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.GetComponent<IsotopeGeneratorComponent>(STarget!.Value).CapacitorRating, Is.EqualTo(4f)));
    }

    [Test]
    public async Task IsotopeOutputAndHeat()
    {
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.System<MapLoaderSystem>().TryLoadGrid(MapId,
                new ResPath("Maps/Test/Breathing/3by3-20oxy-80nit.yml"), out var grid), Is.True);
            var coordinates = new EntityCoordinates(grid!.Value.Owner, new Vector2(0.5f, 0.5f));
            var entity = SEntMan.SpawnEntity("NFStationaryGeneratorIsotopeStandard", coordinates);
            var generator = SEntMan.GetComponent<IsotopeGeneratorComponent>(entity);
            var supplier = SEntMan.GetComponent<PowerSupplierComponent>(entity);
            var slot = SEntMan.System<ItemSlotsSystem>();
            Assert.That(slot.TryGetSlot(entity, "cell1", out _), Is.True);
            Assert.That(slot.TryGetSlot(entity, "cell2", out _), Is.True);
            Assert.That(slot.TryGetSlot(entity, "shielding", out _), Is.True);
            Assert.That(supplier.MaxSupply, Is.Zero);
            foreach (var slotId in generator.CellSlots)
            {
                var cell = SEntMan.SpawnEntity("NFIsotopeCellCasing", coordinates);
                var cellComponent = SEntMan.GetComponent<IsotopeCellComponent>(cell);
                var material = SEntMan.SpawnEntity(slotId == "cell1" ? "SheetUranium" : "FuelBananium", coordinates);
                Assert.That(SEntMan.System<IsotopeCellSystem>().TryLoadFuel((cell, cellComponent), material), Is.True);
                slot.SetLock(entity, slotId, false);
                Assert.That(slot.TryInsert(entity, slotId, cell, SPlayer), Is.True);
                slot.SetLock(entity, slotId, true);
                Assert.That(cellComponent.State, Is.EqualTo(IsotopeCellState.Active));
                cellComponent.CurrentVariation = 1f;
            }
            var atmosphere = SEntMan.System<AtmosphereSystem>().GetContainingMixture(entity)!;
            var heatOutput = atmosphere.Temperature;
            var heatCapacity = SEntMan.System<AtmosphereSystem>().GetHeatCapacity(atmosphere, true);
            SEntMan.System<IsotopeGeneratorSystem>().UpdateGenerator((entity, generator), 1f);
            Assert.That(supplier.MaxSupply, Is.EqualTo(12500f));
            Assert.That((atmosphere.Temperature - heatOutput) * heatCapacity, Is.EqualTo(350f).Within(1f));
            generator.CapacitorRating = 4;
            SEntMan.System<IsotopeGeneratorSystem>().UpdateGenerator((entity, generator), 0f);
            Assert.That(supplier.MaxSupply, Is.EqualTo(14375f).Within(0.01f));
            var fuel = slot.GetItemOrNull(entity, "cell1")!.Value;
            Assert.That(SEntMan.GetComponent<RadiationSourceComponent>(fuel).Intensity, Is.EqualTo(0.5f));
            slot.SetLock(entity, "cell1", false);
            Assert.That(slot.TryEject(entity, "cell1", SPlayer, out _), Is.True);
            var elapsed = SEntMan.GetComponent<IsotopeCellComponent>(fuel).RemainingLifetime;
            SEntMan.System<IsotopeCellSystem>().UpdateCell((fuel, SEntMan.GetComponent<IsotopeCellComponent>(fuel)), 10f);
            Assert.That(SEntMan.GetComponent<IsotopeCellComponent>(fuel).RemainingLifetime, Is.EqualTo(elapsed - 10f));
            Assert.That(supplier.MaxSupply, Is.EqualTo(8625f).Within(0.01f));
            fuel = slot.GetItemOrNull(entity, "cell2")!.Value;
            SEntMan.System<IsotopeCellSystem>().UpdateCell((fuel, SEntMan.GetComponent<IsotopeCellComponent>(fuel)), 9000f);
            heatOutput = atmosphere.Temperature;
            SEntMan.System<IsotopeGeneratorSystem>().UpdateGenerator((entity, generator), 1f);
            Assert.That(supplier.MaxSupply, Is.Zero);
            Assert.That(supplier.Enabled, Is.False);
            Assert.That(atmosphere.Temperature, Is.EqualTo(heatOutput));
        });
    }

    [Test]
    public async Task ActiveCellRejectsReclamation()
    {
        await Server.WaitAssertion(() =>
        {
            var coordinates = SEntMan.GetCoordinates(TargetCoords);
            var entity = SEntMan.SpawnEntity("NFIsotopeCellCasing", coordinates);
            var cell = SEntMan.GetComponent<IsotopeCellComponent>(entity);
            var material = SEntMan.SpawnEntity("SheetUranium", coordinates);
            Assert.That(SEntMan.System<IsotopeCellSystem>().TryLoadFuel((entity, cell), material), Is.True);
            var reclaimAttempt = new MaterialReclaimAttemptEvent();
            SEntMan.EventBus.RaiseLocalEvent(entity, ref reclaimAttempt);
            Assert.That(reclaimAttempt.Cancelled, Is.True);
            SEntMan.System<IsotopeCellSystem>().ActivateCell((entity, cell));
            var machine = SEntMan.SpawnEntity(null, coordinates);
            var arguments = SEntMan.AddComponent<MaterialReclaimerComponent>(machine);
            var startAttempt = new Content.Shared.Power.PowerChangedEvent(true, 1000f);
            SEntMan.EventBus.RaiseLocalEvent(machine, ref startAttempt);
            var generator = SEntMan.System<MaterialReclaimerSystem>();
            Assert.That(generator.TryStartProcessItem(machine, entity, arguments), Is.False);
            var shielding = SEntMan.SpawnEntity(null, coordinates);
            var slot = SEntMan.System<SharedContainerSystem>().EnsureContainer<Container>(shielding, "cell");
            SEntMan.System<SharedContainerSystem>().Insert(entity, slot);
            Assert.That(generator.TryStartProcessItem(machine, shielding, arguments), Is.False);
            SEntMan.System<IsotopeCellSystem>().DepleteCell((entity, cell));
            reclaimAttempt = new MaterialReclaimAttemptEvent();
            SEntMan.EventBus.RaiseLocalEvent(entity, ref reclaimAttempt);
            Assert.That(reclaimAttempt.Cancelled, Is.False);
            Assert.That(SEntMan.GetComponent<PhysicalCompositionComponent>(entity).MaterialComposition.ContainsKey("Uranium"), Is.False);
            Assert.That(generator.TryStartProcessItem(machine, entity, arguments), Is.True);
        });
    }

    [Test]
    public async Task ExamineRespectsHatch()
    {
        await SpawnTarget("NFStationaryGeneratorIsotopeCompact");
        await InteractUsing(Screw);
        await Server.WaitAssertion(() =>
        {
            var markup = SEntMan.System<ExamineSystemShared>().GetExamineText(STarget!.Value, SPlayer).ToString();
            Assert.That(markup, Does.Contain("It has an empty isotope cell bay."));
            Assert.That(markup, Does.Not.Contain("shielding insert covering"));
            Assert.That(SEntMan.GetComponent<DamageableComponent>(STarget.Value).DamageModifierSetId, Is.EqualTo("Metallic"));
            Assert.That(SEntMan.GetComponent<RequireProjectileTargetComponent>(STarget.Value).Active, Is.False);
            Assert.That(markup, Does.Contain("Its casing"));
        });
        await InteractUsing(Screw);
        await Server.WaitAssertion(() =>
        {
            var entity = STarget!.Value;
            var cell = SEntMan.SpawnEntity("NFIsotopeCellCasing", SEntMan.GetCoordinates(TargetCoords));
            var cellComponent = SEntMan.GetComponent<IsotopeCellComponent>(cell);
            var material = SEntMan.SpawnEntity("SheetUranium", SEntMan.GetCoordinates(TargetCoords));
            Assert.That(SEntMan.System<IsotopeCellSystem>().TryLoadFuel((cell, cellComponent), material), Is.True);
            var slot = SEntMan.System<ItemSlotsSystem>();
            slot.SetLock(entity, "cell1", false);
            Assert.That(slot.TryInsert(entity, "cell1", cell, SPlayer), Is.True);
            slot.SetLock(entity, "cell1", true);
            var markup = SEntMan.System<ExamineSystemShared>().GetExamineText(entity, SPlayer).ToString();
            Assert.That(markup, Does.Contain("warm"));
            Assert.That(markup, Does.Contain("The isotope cell bay is covered with a panel."));
            Assert.That(markup, Does.Not.Contain("Cell gauge reads"));
            Assert.That(markup, Does.Not.Contain("shielding label"));
            Assert.That(markup, Does.Not.Contain("Available electrical output"));
        });
        await InteractUsing(Screw);
        await Server.WaitAssertion(() =>
        {
            var markup = SEntMan.System<ExamineSystemShared>().GetExamineText(STarget!.Value, SPlayer).ToString();
            Assert.That(markup, Does.Contain("It has a Uranium isotope cell."));
            Assert.That(markup, Does.Contain("cell bay 1 gauge reads"));
            Assert.That(markup, Does.Contain("shielding label"));
            Assert.That(markup, Does.Not.Contain("shielding insert covering"));
            Assert.That(markup, Does.Not.Contain("Available electrical output"));
            Assert.That(markup, Does.Not.Contain("isotope-slot-"));
            var arguments = new ExaminedEvent(new FormattedMessage(), STarget.Value, SPlayer, false, false);
            SEntMan.EventBus.RaiseLocalEvent(STarget.Value, arguments);
            Assert.That(arguments.GetTotalMessage().ToString(), Does.Not.Contain("gauge reads"));
        });
        await Delete(Target!.Value);
        await SpawnTarget("NFStationaryGeneratorIsotopeStandard");
        await InteractUsing(Screw);
        await Server.WaitAssertion(() =>
        {
            var entity = STarget!.Value;
            var markup = SEntMan.System<ExamineSystemShared>().GetExamineText(entity, SPlayer).ToString();
            Assert.That(markup, Does.Contain("It has two empty isotope cell bays."));
            var cell = SEntMan.SpawnEntity("NFIsotopeCellCasing", SEntMan.GetCoordinates(TargetCoords));
            var cellComponent = SEntMan.GetComponent<IsotopeCellComponent>(cell);
            var material = SEntMan.SpawnEntity("SheetUranium", SEntMan.GetCoordinates(TargetCoords));
            Assert.That(SEntMan.System<IsotopeCellSystem>().TryLoadFuel((cell, cellComponent), material), Is.True);
            var slot = SEntMan.System<ItemSlotsSystem>();
            slot.SetLock(entity, "cell1", false);
            Assert.That(slot.TryInsert(entity, "cell1", cell, SPlayer), Is.True);
            slot.SetLock(entity, "cell1", true);
            markup = SEntMan.System<ExamineSystemShared>().GetExamineText(entity, SPlayer).ToString();
            Assert.That(markup, Does.Contain("It has a Uranium isotope cell and an empty bay."));
            var part = SEntMan.SpawnEntity("NFRadiationShieldingInsertR2", SEntMan.GetCoordinates(TargetCoords));
            slot.SetLock(entity, "shielding", false);
            Assert.That(slot.TryInsert(entity, "shielding", part, SPlayer), Is.True);
            slot.SetLock(entity, "shielding", true);
            markup = SEntMan.System<ExamineSystemShared>().GetExamineText(entity, SPlayer).ToString();
            Assert.That(markup, Does.Contain("You see the shielding insert covering the cell bays."));
            Assert.That(markup, Does.Contain("R2"));
        });
    }

    [Test]
    public async Task MultitoolReadsOutput()
    {
        await SpawnTarget("NFStationaryGeneratorIsotopeCompact");
        await Server.WaitAssertion(() =>
        {
            var entity = STarget!.Value;
            var part = SEntMan.SpawnEntity("Multitool", SEntMan.GetCoordinates(TargetCoords));
            var arguments = new GetVerbsEvent<ExamineVerb>(SPlayer, entity, null, Hands, true, true, true, new());
            SEntMan.EventBus.RaiseLocalEvent(entity, arguments);
            Assert.That(arguments.Verbs.Single(verb => verb.Text == "Read generator output").Disabled, Is.True);
            arguments = new GetVerbsEvent<ExamineVerb>(SPlayer, entity, part, Hands, true, true, true, new());
            SEntMan.EventBus.RaiseLocalEvent(entity, arguments);
            Assert.That(arguments.Verbs.Single(verb => verb.Text == "Read generator output").Disabled, Is.False);
            arguments = new GetVerbsEvent<ExamineVerb>(SPlayer, entity, part, Hands, true, true, false, new());
            SEntMan.EventBus.RaiseLocalEvent(entity, arguments);
            Assert.That(arguments.Verbs.Any(verb => verb.Text == "Read generator output"), Is.False);
            var cell = SEntMan.SpawnEntity("NFIsotopeCellCasing", SEntMan.GetCoordinates(TargetCoords));
            var cellComponent = SEntMan.GetComponent<IsotopeCellComponent>(cell);
            var material = SEntMan.SpawnEntity("SheetUranium", SEntMan.GetCoordinates(TargetCoords));
            Assert.That(SEntMan.System<IsotopeCellSystem>().TryLoadFuel((cell, cellComponent), material), Is.True);
            var slot = SEntMan.System<ItemSlotsSystem>();
            slot.SetLock(entity, "cell1", false);
            Assert.That(slot.TryInsert(entity, "cell1", cell, SPlayer), Is.True);
            slot.SetLock(entity, "cell1", true);
            var startAttempt = new InteractUsingEvent(SPlayer, part, entity, SEntMan.GetCoordinates(TargetCoords));
            SEntMan.EventBus.RaiseLocalEvent(entity, startAttempt);
            Assert.That(startAttempt.Handled, Is.True);
            Assert.That(SEntMan.System<IsotopeGeneratorSystem>().GetDiagnosticReadout(entity), Does.Contain("5 kW"));
            Assert.That(SEntMan.System<ExamineSystemShared>().GetExamineText(entity, SPlayer).ToString(),
                Does.Not.Contain("Available electrical output"));
        });
        await InteractUsing("Multitool");
    }}



