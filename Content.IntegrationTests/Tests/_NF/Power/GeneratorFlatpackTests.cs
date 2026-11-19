// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._NF.Power.FuelModules;
using Content.Server.Construction.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared._NF.Power.Isotope;
using Content.Shared.Construction;
using Content.Shared.Construction.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Materials;
using Content.Shared.Power.Generator;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._NF.Power;

public sealed class GeneratorFlatpackTests : InteractionTest
{
    [TestCase("CombustionStandard")]
    [TestCase("CombustionCommercial")]
    [TestCase("GasTurbineCommercial")]
    [TestCase("IsotopeCompact")]
    [TestCase("IsotopeStandard")]
    [TestCase("SteamStandard")]
    [TestCase("SteamCommercial")]
    [TestCase("StirlingCompact")]
    [TestCase("StirlingStandard")]
    public async Task FlatpackerPrintsAndUnpacksStationaryGenerator(string model)
    {
        var prefix = "NFStationaryGenerator" + model;
        var generatorPrototype = prefix + "Empty";
        var unpackCoordinates = new EntityCoordinates(MapData.Grid.Owner, new Vector2(0.5f, 1.5f));
        var rotation = Angle.FromDegrees(90);
        await Server.WaitAssertion(() =>
        {
            for (var x = -3; x <= 4; x++)
            for (var y = -3; y <= 4; y++)
                MapSystem.SetTile(MapData.Grid, new Vector2i(x, y), new Tile(TileMan[Plating].TileId));
        });
        await SpawnTarget("MachineFlatpacker");
        await Server.WaitPost(() => SEntMan.System<PowerReceiverSystem>().SetNeedsPower(STarget!.Value, false));
        await InteractUsing(prefix + "MachineCircuitboard");
        await Server.WaitAssertion(() =>
        {
            var packer = STarget!.Value;
            var creator = SEntMan.GetComponent<FlatpackCreatorComponent>(packer);
            var slots = SEntMan.System<ItemSlotsSystem>();
            Assert.That(slots.TryGetSlot(packer, creator.SlotId, out var slot), Is.True);
            Assert.That(slot!.Item, Is.Not.Null);
            var board = slot.Item!.Value;
            var machineBoard = SEntMan.GetComponent<MachineBoardComponent>(board);
            var costs = SEntMan.System<SharedFlatpackSystem>().GetFlatpackCreationCost((packer, creator), (board, machineBoard));
            var stock = costs.ToDictionary(cost => cost.Key, cost => -cost.Value);
            Assert.That(SEntMan.System<SharedMaterialStorageSystem>().TryChangeMaterialAmount((packer, null), stock), Is.True);
            Assert.That(SEntMan.System<SharedUserInterfaceSystem>().TryOpenUi(packer, FlatpackCreatorUIKey.Key, SPlayer), Is.True);
        });
        await RunTicks(5);
        await Client.WaitPost(() => CUiSys.ClientSendUiMessage(CTarget!.Value, FlatpackCreatorUIKey.Key,
            new FlatpackCreatorStartPackBuiMessage()));
        await RunTicks(5);
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.GetComponent<FlatpackCreatorComponent>(STarget!.Value).Packing, Is.True));

        await RunTicks(240);
        var package = EntityUid.Invalid;
        await Server.WaitAssertion(() =>
        {
            var packerCoordinates = SEntMan.GetComponent<TransformComponent>(STarget!.Value).Coordinates;
            var flatpack = SEntMan.System<EntityLookupSystem>()
                .GetEntitiesInRange<FlatpackComponent>(packerCoordinates, 0.25f).Single();
            Assert.That(flatpack.Comp.Entity?.Id, Is.EqualTo(generatorPrototype));
            package = flatpack.Owner;
            Transform.SetCoordinates(package, unpackCoordinates);
            Transform.SetLocalRotation(package, rotation);
            Target = SEntMan.GetNetEntity(package);
            TargetCoords = SEntMan.GetNetCoordinates(unpackCoordinates);
        });
        await RunTicks(5);
        await Client.WaitAssertion(() =>
            Assert.That(CEntMan.GetComponent<FlatpackComponent>(CTarget!.Value).Entity?.Id, Is.EqualTo(generatorPrototype)));

        if (model == "GasTurbineCommercial")
        {
            EntityUid wall = default;
            await Server.WaitPost(() =>
                wall = SEntMan.SpawnEntity("WallSolid", unpackCoordinates.Offset(rotation.RotateVec(Vector2.UnitY))));
            await InteractUsing("Multitool");
            AssertExists();
            await Server.WaitPost(() => SEntMan.DeleteEntity(wall));
        }

        await InteractUsing("Multitool");
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.Deleted(package), Is.True);
            var generator = SEntMan.System<EntityLookupSystem>()
                .GetEntitiesInRange<MachineComponent>(unpackCoordinates, 0.25f).Single();
            Assert.That(SEntMan.GetComponent<MetaDataComponent>(generator.Owner).EntityPrototype!.ID, Is.EqualTo(generatorPrototype));
            Assert.That(SEntMan.GetComponent<TransformComponent>(generator.Owner).LocalRotation.Degrees, Is.EqualTo(90).Within(0.01));
            Assert.That(generator.Comp.BoardContainer.ContainedEntities, Has.Count.EqualTo(1));
            if (SEntMan.TryGetComponent(generator.Owner, out FuelGeneratorComponent? fuelGenerator))
            {
                Assert.That(fuelGenerator.On, Is.False);
                Assert.That(SEntMan.System<FuelModuleSystem>().GetInstalledModule(generator.Owner), Is.Null);
            }
            if (SEntMan.TryGetComponent(generator.Owner, out IsotopeGeneratorComponent? isotope))
            {
                var slots = SEntMan.System<ItemSlotsSystem>();
                foreach (var slot in isotope.CellSlots)
                    Assert.That(slots.GetItemOrNull(generator.Owner, slot), Is.Null);
            }
        });
    }
}
