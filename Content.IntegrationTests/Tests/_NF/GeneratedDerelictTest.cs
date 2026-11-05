using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._NF.Medical.Components;
using Content.Server._NF.Salvage;
using Content.Server._NF.StationEvents.Components;
using Content.Server._NF.StationEvents.Events;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Decals;
using Content.Server.GameTicking;
using Content.Server.StationEvents.Components;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared.Anomaly.Components;
using Content.Shared.Atmos;
using Content.Shared.Body.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Clothing.Components;
using Content.Shared.Damage;
using Content.Shared.Doors.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Fluids.Components;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Inventory;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Storage;
using Content.Shared.Storage.Components;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.IntegrationTests.Tests._NF;

[TestFixture]
public sealed class GeneratedDerelictTest
{
    private static readonly ProtoId<VesselPrototype> SalvageVessel = "Bodkin";
    private static readonly ProtoId<VesselPrototype> ScienceVessel = "Investigator";
    private static readonly ProtoId<RandomHumanoidSettingsPrototype> ServiceCasualty = "MedicalBountyService";

    [TestCase(2, "MachineAnomalyVessel", false, 5, "Insuls")]
    [TestCase(9, "MachineArtifactAnalyzer", true, 2, "MurderedPierce")]
    public async Task ScienceIncidentsDamageTheirEquipmentAndPlaceTheCasualtyNearby(
        int seed,
        string expectedAnchor,
        bool expectArtifact,
        int expectedScorchMarks,
        string expectedBounty)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.EntMan;
        var maps = server.System<SharedMapSystem>();
        var loader = server.System<MapLoaderSystem>();
        var derelicts = server.System<DerelictGenerationSystem>();
        var decals = server.System<DecalSystem>();

        await server.WaitAssertion(() =>
        {
            var vessel = server.ProtoMan.Index(ScienceVessel);
            maps.CreateMap(out var mapId);
            try
            {
                Assert.That(loader.TryLoadGrid(mapId, vessel.ShuttlePath, out var loadedGrid), Is.True);
                var grid = loadedGrid!.Value;
                var gridTransform = entities.GetComponent<TransformComponent>(grid.Owner);
                var children = gridTransform.ChildEnumerator;
                var anchor = EntityUid.Invalid;
                while (children.MoveNext(out var child))
                {
                    if (entities.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID == expectedAnchor)
                        anchor = child;
                }

                Assert.That(anchor, Is.Not.EqualTo(EntityUid.Invalid));
                var anchorDamage = entities.GetComponent<DamageableComponent>(anchor);
                var damageBefore = anchorDamage.TotalDamage;
                var anchorPosition = entities.GetComponent<TransformComponent>(anchor).LocalPosition;
                var decalBounds = new Box2(anchorPosition - new Vector2(6), anchorPosition + new Vector2(6));
                var scorchMarksBefore = decals.GetDecalsIntersecting(grid.Owner, decalBounds).Count(entry => entry.Decal.Id == "burnt1");
                var tilesBefore = maps.GetAllTiles(grid.Owner, grid.Comp).Count();
                Assert.That(derelicts.TryGenerate(grid, vessel, seed), Is.True);
                Assert.That(anchorDamage.TotalDamage, Is.GreaterThan(damageBefore));
                Assert.That(maps.GetAllTiles(grid.Owner, grid.Comp).Count(), Is.EqualTo(tilesBefore));
                var scorchMarksAfter = decals.GetDecalsIntersecting(grid.Owner, decalBounds).Count(entry => entry.Decal.Id == "burnt1");
                Assert.That(scorchMarksAfter - scorchMarksBefore, Is.EqualTo(expectedScorchMarks));

                var artifactFound = false;
                var casualtyFound = false;
                children = gridTransform.ChildEnumerator;
                while (children.MoveNext(out var child))
                {
                    var isArtifact = entities.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID == "ComplexXenoArtifactItem";
                    var isCasualty = entities.HasComponent<MedicalBountyComponent>(child);
                    if (!isArtifact && !isCasualty)
                        continue;

                    var position = entities.GetComponent<TransformComponent>(child).LocalPosition;
                    Assert.That((position - anchorPosition).Length(), Is.LessThanOrEqualTo(5.01f));
                    artifactFound |= isArtifact;
                    casualtyFound |= isCasualty;
                    if (isCasualty)
                    {
                        Assert.That(entities.GetComponent<MobStateComponent>(child).CurrentState, Is.EqualTo(MobState.Dead));
                        Assert.That(entities.GetComponent<MedicalBountyComponent>(child).Bounty?.ID, Is.EqualTo(expectedBounty));
                    }
                }

                Assert.That(casualtyFound, Is.True);
                Assert.That(artifactFound, Is.EqualTo(expectArtifact));
                AssertScienceCache(entities, grid.Owner);
            }
            finally
            {
                maps.DeleteMap(mapId);
            }
        });

        await pair.CleanReturnAsync();
    }

    [TestCase("Bodkin", 1, "MildBruteMix", true)]
    [TestCase("Investigator", 5, "Suffocated", false)]
    [TestCase("Stasis", 5, "MurderedSlash", true)]
    public async Task ExposedCasualtiesUseOneEvaLockerAndKeepTheirInjuries(
        string vesselId,
        int seed,
        string expectedBounty,
        bool expectThreat)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.EntMan;
        var maps = server.System<SharedMapSystem>();
        var loader = server.System<MapLoaderSystem>();
        var derelicts = server.System<DerelictGenerationSystem>();
        var atmosphere = server.System<AtmosphereSystem>();
        var inventory = server.System<InventorySystem>();
        var transforms = server.System<SharedTransformSystem>();
        var mapId = MapId.Nullspace;
        var casualty = EntityUid.Invalid;
        var initialDamage = FixedPoint2.Zero;

        await server.WaitAssertion(() =>
        {
            var vessel = server.ProtoMan.Index(new ProtoId<VesselPrototype>(vesselId));
            var map = maps.CreateMap(out mapId);
            Assert.That(loader.TryLoadGrid(mapId, vessel.ShuttlePath, out var loadedGrid), Is.True);
            var grid = loadedGrid!.Value;
            var stockedLockers = new Dictionary<BaseContainer, EntityUid[]>();
            var helmets = new Dictionary<EntityUid, EntityUid>();
            var gridTransform = entities.GetComponent<TransformComponent>(grid.Owner);
            var children = gridTransform.ChildEnumerator;
            while (children.MoveNext(out var child))
            {
                if (entities.TryGetComponent<EntityStorageComponent>(child, out var closet) && closet.Contents.ContainedEntities.Count > 0)
                    stockedLockers.Add(closet.Contents, closet.Contents.ContainedEntities.ToArray());
                if (entities.TryGetComponent<StorageComponent>(child, out var locker) && locker.Container.ContainedEntities.Count > 0)
                    stockedLockers.Add(locker.Container, locker.Container.ContainedEntities.ToArray());
            }
            foreach (var items in stockedLockers.Values)
            {
                foreach (var item in items)
                {
                    if (entities.TryGetComponent<ToggleableClothingComponent>(item, out var toggleable) &&
                        toggleable.ClothingUid is { } attachedHelmet)
                        helmets.Add(item, attachedHelmet);
                }
            }

            Assert.That(derelicts.TryGenerate(grid, vessel, seed), Is.True);
            var changedLockers = stockedLockers.Where(entry => !entry.Key.ContainedEntities.SequenceEqual(entry.Value)).ToArray();
            Assert.That(changedLockers, Has.Length.EqualTo(1));
            var emptiedLocker = changedLockers.Single();
            Assert.That(emptiedLocker.Key.ContainedEntities, Is.Empty);

            children = gridTransform.ChildEnumerator;
            var hasThreat = false;
            while (children.MoveNext(out var child))
            {
                if (entities.HasComponent<MedicalBountyComponent>(child))
                    casualty = child;
                hasThreat |= entities.HasComponent<NFSalvageMobRestrictionsComponent>(child);
            }

            Assert.That(hasThreat, Is.EqualTo(expectThreat));
            if (vesselId == "Investigator")
                AssertScienceCache(entities, grid.Owner);
            Assert.That(casualty, Is.Not.EqualTo(EntityUid.Invalid));
            Assert.That(entities.GetComponent<MedicalBountyComponent>(casualty).Bounty?.ID, Is.EqualTo(expectedBounty));
            Assert.That(inventory.TryGetSlotEntity(casualty, "outerClothing", out var suit), Is.True);
            Assert.That(inventory.TryGetSlotEntity(casualty, "head", out var helmet), Is.True);
            Assert.That(emptiedLocker.Value, Does.Contain(suit!.Value));
            Assert.That(helmet, Is.EqualTo(helmets[suit.Value]));

            transforms.SetCoordinates(casualty, new EntityCoordinates(map, new Vector2(1000, 1000)));
            Assert.That(atmosphere.GetTileMixture(casualty)?.Pressure, Is.LessThan(Atmospherics.HazardLowPressure));
            initialDamage = entities.GetComponent<DamageableComponent>(casualty).TotalDamage;
        });

        await server.WaitRunTicks(180);
        await server.WaitAssertion(() =>
        {
            Assert.That(entities.GetComponent<DamageableComponent>(casualty).TotalDamage, Is.EqualTo(initialDamage));
            Assert.That(entities.GetComponent<MobStateComponent>(casualty).CurrentState, Is.EqualTo(MobState.Dead));
            maps.DeleteMap(mapId);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PlaytestEventSpawnsAndDespawnsGeneratedGrid()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { DummyTicker = false, Dirty = true });
        var server = pair.Server;
        var entities = server.EntMan;
        var ticker = server.System<GameTicker>();

        await server.WaitAssertion(() =>
        {
            var stockRule = ticker.AddGameRule("BluespaceSalvage");
            var rule = ticker.AddGameRule("BluespaceGeneratedSalvage");
            var stockEvent = entities.GetComponent<StationEventComponent>(stockRule);
            var generatedEvent = entities.GetComponent<StationEventComponent>(rule);
            Assert.That(entities.GetComponent<MetaDataComponent>(rule).EntityName,
                Is.EqualTo(entities.GetComponent<MetaDataComponent>(stockRule).EntityName));
            Assert.That(generatedEvent.StartRadioAnnouncement, Is.EqualTo(stockEvent.StartRadioAnnouncement));
            Assert.That(generatedEvent.WarningRadioAnnouncement, Is.EqualTo(stockEvent.WarningRadioAnnouncement));
            Assert.That(generatedEvent.EndRadioAnnouncement, Is.EqualTo(stockEvent.EndRadioAnnouncement));
            entities.DeleteEntity(stockRule);
            ticker.StartGameRule(rule);
            ticker.StartGameRule(rule); // Skip the station event's normal delay.
            var eventState = entities.GetComponent<BluespaceErrorRuleComponent>(rule);
            Assert.That(eventState.GridsUid, Has.Count.EqualTo(1));

            var grid = eventState.GridsUid.Single();
            var gridTransform = entities.GetComponent<TransformComponent>(grid);
            Assert.That(gridTransform.MapID, Is.EqualTo(ticker.DefaultMap));
            var children = gridTransform.ChildEnumerator;
            EntityUid? casualty = null;
            while (children.MoveNext(out var child))
            {
                if (entities.HasComponent<MedicalBountyComponent>(child))
                    casualty = child;
            }

            Assert.That(casualty, Is.Not.Null, "Expected a generated wreck with a bounty casualty.");
            Assert.That(ticker.EndGameRule(rule), Is.True);
            Assert.That(entities.EntityExists(grid), Is.False);
            Assert.That(entities.EntityExists(casualty), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task ExposedCrewUsesSeparateStockedLockersAndAcceptsSeparateHelmets(int kitCount)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.EntMan;
        var maps = server.System<SharedMapSystem>();
        var loader = server.System<MapLoaderSystem>();
        var derelicts = server.System<DerelictGenerationSystem>();
        var containers = server.System<SharedContainerSystem>();
        var inventory = server.System<InventorySystem>();

        await server.WaitAssertion(() =>
        {
            var vessel = server.ProtoMan.Index(SalvageVessel);
            if (kitCount < 2)
            {
                Assert.That(Generate(1, kitCount), Is.EqualTo(kitCount));
                return;
            }

            int[] xenoSeeds = [0, 4, 13, 14, 16, 24];
            var crewSeed = xenoSeeds.First(seed => Generate(seed, 2) == 2);
            Assert.That(Generate(crewSeed, 1), Is.EqualTo(1));

            int Generate(int seed, int stockedKits)
            {
                maps.CreateMap(out var mapId);
                try
                {
                    Assert.That(loader.TryLoadGrid(mapId, vessel.ShuttlePath, out var loadedGrid), Is.True);
                    var grid = loadedGrid!.Value;
                    var transform = entities.GetComponent<TransformComponent>(grid.Owner);
                    var children = transform.ChildEnumerator;
                    var testLockers = new List<BaseContainer>();
                    while (children.MoveNext(out var child))
                    {
                        if (entities.TryGetComponent<EntityStorageComponent>(child, out var closet))
                        {
                            foreach (var item in closet.Contents.ContainedEntities.ToArray())
                                entities.DeleteEntity(item);
                            if (entities.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID is
                                "SuitStorageWallmountSalv" or "SuitStorageQuartermaster")
                            {
                                testLockers.Add(closet.Contents);
                            }
                        }
                        if (entities.TryGetComponent<StorageComponent>(child, out var locker))
                        {
                            foreach (var item in locker.Container.ContainedEntities.ToArray())
                                entities.DeleteEntity(item);
                        }
                    }

                    Assert.That(testLockers, Has.Count.GreaterThanOrEqualTo(2));
                    var kits = new Dictionary<EntityUid, (EntityUid Helmet, BaseContainer Locker)>();
                    foreach (var locker in testLockers.Take(stockedKits))
                    {
                        var coordinates = new EntityCoordinates(locker.Owner, Vector2.Zero);
                        var suit = entities.SpawnEntity("ClothingOuterHardsuitEVA", coordinates);
                        var helmet = entities.SpawnEntity("ClothingHeadHelmetEVA", coordinates);
                        Assert.That(containers.Insert(suit, locker), Is.True);
                        Assert.That(containers.Insert(helmet, locker), Is.True);
                        kits.Add(suit, (helmet, locker));
                    }

                    Assert.That(derelicts.TryGenerate(grid, vessel, seed), Is.EqualTo(stockedKits > 0));
                    var casualtyTiles = new HashSet<Vector2i>();
                    children = transform.ChildEnumerator;
                    while (children.MoveNext(out var child))
                    {
                        if (!entities.TryGetComponent<MedicalBountyComponent>(child, out var bounty))
                            continue;

                        Assert.That(bounty.BountyInitialized, Is.True);
                        Assert.That(entities.GetComponent<MobStateComponent>(child).CurrentState, Is.EqualTo(MobState.Dead));
                        Assert.That(inventory.TryGetSlotEntity(child, "outerClothing", out var equippedSuit), Is.True);
                        Assert.That(inventory.TryGetSlotEntity(child, "head", out var equippedHelmet), Is.True);
                        Assert.That(kits.Remove(equippedSuit!.Value, out var kit), Is.True);
                        Assert.That(equippedHelmet, Is.EqualTo(kit.Helmet));
                        Assert.That(kit.Locker.ContainedEntities, Is.Empty);
                        Assert.That(casualtyTiles.Add(maps.TileIndicesFor(grid, entities.GetComponent<TransformComponent>(child).Coordinates)), Is.True);
                    }

                    foreach (var (suit, (helmet, locker)) in kits)
                        Assert.That(locker.ContainedEntities, Is.EquivalentTo(new[] { suit, helmet }));

                    return casualtyTiles.Count;
                }
                finally
                {
                    maps.DeleteMap(mapId);
                }
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task WreckWearExtendsBeyondThePrimaryIncident()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.EntMan;
        var maps = server.System<SharedMapSystem>();
        var loader = server.System<MapLoaderSystem>();
        var derelicts = server.System<DerelictGenerationSystem>();
        var decals = server.System<DecalSystem>();
        var lights = server.System<SharedPoweredLightSystem>();

        await server.WaitAssertion(() =>
        {
            var vessel = server.ProtoMan.Index(SalvageVessel);
            maps.CreateMap(out var mapId);
            try
            {
                Assert.That(loader.TryLoadGrid(mapId, vessel.ShuttlePath, out var loadedGrid), Is.True);
                var grid = loadedGrid!.Value;
                var tileCount = maps.GetAllTiles(grid.Owner, grid.Comp).Count();
                var damagedPlating = server.ResolveDependency<ITileDefinitionManager>()["PlatingDamaged"].TileId;
                var platingBefore = CountDamagedPlating();
                var children = entities.GetComponent<TransformComponent>(grid.Owner).ChildEnumerator;
                var workingBulbs = new List<EntityUid>();
                while (children.MoveNext(out var child))
                {
                    if (entities.TryGetComponent<PoweredLightComponent>(child, out var fixture) &&
                        lights.GetBulb(child, fixture) is { } bulb &&
                        entities.GetComponent<LightBulbComponent>(bulb).State != LightBulbState.Broken)
                    {
                        workingBulbs.Add(bulb);
                    }
                }

                var bounds = new Box2(-64, -64, 64, 64);
                var originalDecals = decals.GetDecalsIntersecting(grid.Owner, bounds).Select(entry => entry.Index).ToHashSet();
                Assert.That(derelicts.TryGenerate(grid, vessel, 9), Is.True);
                Assert.That(maps.GetAllTiles(grid.Owner, grid.Comp).Count(), Is.EqualTo(tileCount));
                Assert.That(CountDamagedPlating(), Is.GreaterThan(platingBefore));
                Assert.That(workingBulbs.Any(bulb => entities.GetComponent<LightBulbComponent>(bulb).State == LightBulbState.Broken), Is.True);
                var remoteWear = decals.GetDecalsIntersecting(grid.Owner, bounds).Where(entry =>
                    !originalDecals.Contains(entry.Index) && (entry.Decal.Coordinates - new Vector2(2, -10)).Length() > 6);
                Assert.That(remoteWear.Select(entry => entry.Decal.Id), Does.Contain("Rust"));
                Assert.That(remoteWear.Select(entry => entry.Decal.Id), Does.Contain("DirtHeavyMonotile"));

                int CountDamagedPlating() => maps.GetAllTiles(grid.Owner, grid.Comp).Count(tile => tile.Tile.TypeId == damagedPlating);
            }
            finally
            {
                maps.DeleteMap(mapId);
            }
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(9, false)]
    [TestCase(1, true)]
    public async Task SalvageScenesReplayTheirBreachCasualtyAndLoot(int seed, bool expectThreat)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.EntMan;
        var maps = server.System<SharedMapSystem>();
        var loader = server.System<MapLoaderSystem>();
        var derelicts = server.System<DerelictGenerationSystem>();

        await server.WaitAssertion(() =>
        {
            var vessel = server.ProtoMan.Index(SalvageVessel);
            var scene = Generate(seed);
            var replay = Generate(seed, scene.Casualty);
            Assert.That(scene.HasThreat, Is.EqualTo(expectThreat));
            Assert.That(replay.HasThreat, Is.EqualTo(scene.HasThreat));
            Assert.That(replay.Breaches, Is.EqualTo(scene.Breaches));
            Assert.That(replay.Casualty, Is.EqualTo(scene.Casualty));
            Assert.That(replay.Aftermath, Is.EquivalentTo(scene.Aftermath));

            (bool HasThreat, Vector2i[] Breaches, Vector2i Casualty, List<(string, Vector2i)> Aftermath) Generate(
                int sceneSeed, Vector2i? clutterTile = null)
            {
                maps.CreateMap(out var mapId);
                try
                {
                    Assert.That(loader.TryLoadGrid(mapId, vessel.ShuttlePath, out var loadedGrid), Is.True);
                    var grid = loadedGrid!.Value;
                    var clutter = clutterTile is { } position
                        ? entities.SpawnEntity("Crowbar", maps.GridTileToLocal(grid.Owner, grid.Comp, position))
                        : (EntityUid?) null;
                    var tilesBefore = maps.GetAllTiles(grid.Owner, grid.Comp).Count();
                    var initialWalls = new Dictionary<EntityUid, Vector2i>();
                    var initialEntities = new HashSet<EntityUid>();
                    var initialMachinery = new List<EntityUid>();
                    var transform = entities.GetComponent<TransformComponent>(grid.Owner);
                    var children = transform.ChildEnumerator;
                    while (children.MoveNext(out var child))
                    {
                        initialEntities.Add(child);
                        var prototypeId = entities.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID;
                        if (prototypeId == "WallShuttle")
                            initialWalls.Add(child, maps.TileIndicesFor(grid, entities.GetComponent<TransformComponent>(child).Coordinates));
                        if (prototypeId is "OreProcessor" or "ScrapProcessor" or "MaterialReclaimer")
                            initialMachinery.Add(child);
                    }

                    Assert.That(derelicts.TryGenerate(grid, vessel, sceneSeed), Is.True, $"Generation failed for seed {sceneSeed}.");
                    if (clutter is { } tool)
                    {
                        var movedTile = maps.TileIndicesFor(grid, entities.GetComponent<TransformComponent>(tool).Coordinates);
                        Assert.That(movedTile, Is.Not.EqualTo(clutterTile));
                    }
                    var removedWalls = initialWalls.Where(entry => !entities.EntityExists(entry.Key))
                        .Select(entry => entry.Value).OrderBy(position => position.X).ThenBy(position => position.Y).ToArray();
                    Assert.That(removedWalls.Length, Is.InRange(1, 2));
                    Assert.That(maps.GetAllTiles(grid.Owner, grid.Comp).Count(), Is.EqualTo(tilesBefore));

                    var casualties = new List<Vector2i>();
                    var aftermath = new List<(string, Vector2i)>();
                    var hasThreat = false;
                    children = transform.ChildEnumerator;
                    while (children.MoveNext(out var child))
                    {
                        if (entities.TryGetComponent<MedicalBountyComponent>(child, out var bounty))
                        {
                            Assert.That(bounty.BountyInitialized, Is.True);
                            Assert.That(entities.GetComponent<MobStateComponent>(child).CurrentState, Is.EqualTo(MobState.Dead));
                            casualties.Add(maps.TileIndicesFor(grid, entities.GetComponent<TransformComponent>(child).Coordinates));
                            continue;
                        }

                        if (!initialEntities.Contains(child) && !entities.HasComponent<PuddleComponent>(child) &&
                            entities.GetComponent<MetaDataComponent>(child).EntityPrototype is { } prototype)
                            aftermath.Add((prototype.ID, maps.TileIndicesFor(grid, entities.GetComponent<TransformComponent>(child).Coordinates)));

                        if (entities.TryGetComponent<NFSalvageMobRestrictionsComponent>(child, out var restrictions))
                        {
                            Assert.That(restrictions.LinkedGridEntity, Is.EqualTo(grid.Owner));
                            hasThreat = true;
                        }
                    }

                    Assert.That(casualties, Has.Count.EqualTo(1));
                    Assert.That(aftermath, Has.Count.InRange(2, expectThreat ? 7 : 4));
                    if (expectThreat)
                    {
                        Assert.That(aftermath.Count(entry => entry.Item1 == "ScrapGenericBrokenMachine"), Is.EqualTo(1));
                        Assert.That(initialMachinery.Count(entity => !entities.EntityExists(entity)), Is.EqualTo(1));
                    }

                    return (hasThreat, removedWalls, casualties.Single(), aftermath);
                }
                finally
                {
                    maps.DeleteMap(mapId);
                }
            }
        });

        await pair.CleanReturnAsync();
    }

    [TestCase("Garden", 9, "NFDerelictKudzu", 3, "MobTomatoKiller", 1, 0)]
    [TestCase("Bodkin", 2, "NFDerelictTendons", 5, null, 1, 8)]
    [TestCase("Bodkin", 0, "AsteroidRock", 5, "NFMobXeno", 2, 8)]
    public async Task AttacksPlaceThemedAftermathThreatsAndSpeciesBlood(
        string vesselId,
        int seed,
        string expectedAftermath,
        int expectedCount,
        string? expectedThreat,
        int expectedThreatCount,
        int expectedExteriorTiles)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.EntMan;
        var maps = server.System<SharedMapSystem>();
        var loader = server.System<MapLoaderSystem>();
        var derelicts = server.System<DerelictGenerationSystem>();
        var solutions = server.System<SharedSolutionContainerSystem>();

        await server.WaitAssertion(() =>
        {
            var vessel = server.ProtoMan.Index(new ProtoId<VesselPrototype>(vesselId));
            maps.CreateMap(out var mapId);
            try
            {
                Assert.That(loader.TryLoadGrid(mapId, vessel.ShuttlePath, out var loadedGrid), Is.True);
                var grid = loadedGrid!.Value;
                var tilesBefore = maps.GetAllTiles(grid.Owner, grid.Comp).Select(tile => tile.GridIndices).ToHashSet();
                Assert.That(derelicts.TryGenerate(grid, vessel, seed), Is.True);
                var addedTiles = maps.GetAllTiles(grid.Owner, grid.Comp).Select(tile => tile.GridIndices).Except(tilesBefore).ToHashSet();
                Assert.That(addedTiles, Has.Count.EqualTo(expectedExteriorTiles));

                var aftermathCount = 0;
                var threats = new List<string>();
                var puddles = new List<EntityUid>();
                var casualty = EntityUid.Invalid;
                var children = entities.GetComponent<TransformComponent>(grid.Owner).ChildEnumerator;
                while (children.MoveNext(out var child))
                {
                    var prototype = entities.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID;
                    if (prototype == expectedAftermath)
                    {
                        aftermathCount++;
                        if (prototype == "AsteroidRock")
                            Assert.That(addedTiles, Does.Contain(maps.TileIndicesFor(grid, entities.GetComponent<TransformComponent>(child).Coordinates)));
                    }

                    if (entities.HasComponent<MedicalBountyComponent>(child))
                        casualty = child;
                    if (entities.HasComponent<PuddleComponent>(child))
                        puddles.Add(child);

                    if (prototype != null && entities.TryGetComponent<NFSalvageMobRestrictionsComponent>(child, out var restrictions))
                    {
                        Assert.That(restrictions.LinkedGridEntity, Is.EqualTo(grid.Owner));
                        threats.Add(prototype);
                    }
                }

                Assert.That(aftermathCount, Is.EqualTo(expectedCount));
                Assert.That(threats, Has.Count.EqualTo(expectedThreatCount));
                if (expectedThreat != null)
                    Assert.That(threats, Is.All.EqualTo(expectedThreat));
                else
                    Assert.That(threats.Single(), Is.AnyOf("MobFleshClampSalvage", "MobFleshJaredSalvage"));

                Assert.That(casualty, Is.Not.EqualTo(EntityUid.Invalid));
                var casualtyTile = maps.TileIndicesFor(grid, entities.GetComponent<TransformComponent>(casualty).Coordinates);
                var reagent = entities.GetComponent<BloodstreamComponent>(casualty).BloodReagent;
                var hasBlood = puddles.Any(puddle =>
                    maps.TileIndicesFor(grid, entities.GetComponent<TransformComponent>(puddle).Coordinates) == casualtyTile &&
                    solutions.TryGetSolution(puddle, entities.GetComponent<PuddleComponent>(puddle).SolutionName, out _, out var solution) &&
                    solution.Contents.Any(quantity => quantity.Reagent.Prototype == reagent.Id));
                Assert.That(hasBlood, Is.True);
                Assert.That(puddles, Has.Count.GreaterThan(1));
                var totalBlood = puddles.SelectMany(puddle =>
                {
                    return solutions.TryGetSolution(puddle, entities.GetComponent<PuddleComponent>(puddle).SolutionName, out _, out var solution)
                        ? solution.Contents.Where(quantity => quantity.Reagent.Prototype == reagent.Id)
                        : [];
                }).Sum(quantity => quantity.Quantity.Float());
                Assert.That(totalBlood, Is.GreaterThan(10f));
            }
            finally
            {
                maps.DeleteMap(mapId);
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SomeXenoScenesLeaveADroppedWeaponAndXenoBlood()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.EntMan;
        var maps = server.System<SharedMapSystem>();
        var loader = server.System<MapLoaderSystem>();
        var derelicts = server.System<DerelictGenerationSystem>();
        var solutions = server.System<SharedSolutionContainerSystem>();

        await server.WaitAssertion(() =>
        {
            int[] xenoSeeds = [0, 4, 13, 14, 16, 24];
            Assert.That(xenoSeeds.Any(HasDefenseEvidence), Is.True);

            bool HasDefenseEvidence(int seed)
            {
                var vessel = server.ProtoMan.Index(SalvageVessel);
                maps.CreateMap(out var mapId);
                try
                {
                    Assert.That(loader.TryLoadGrid(mapId, vessel.ShuttlePath, out var loadedGrid), Is.True);
                    var grid = loadedGrid!.Value;
                    var gridTransform = entities.GetComponent<TransformComponent>(grid.Owner);
                    var originalEntities = new HashSet<EntityUid>();
                    var children = gridTransform.ChildEnumerator;
                    while (children.MoveNext(out var child))
                        originalEntities.Add(child);

                    Assert.That(derelicts.TryGenerate(grid, vessel, seed), Is.True);
                    var weaponFound = false;
                    var enemyBloodFound = false;
                    children = gridTransform.ChildEnumerator;
                    while (children.MoveNext(out var child))
                    {
                        if (originalEntities.Contains(child))
                            continue;

                        weaponFound |= entities.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID == "Crowbar";
                        enemyBloodFound |= entities.TryGetComponent<PuddleComponent>(child, out var puddle) &&
                            solutions.TryGetSolution(child, puddle.SolutionName, out _, out var solution) &&
                            solution.GetTotalPrototypeQuantity("FluorosulfuricAcid") > FixedPoint2.Zero;
                    }

                    Assert.That(enemyBloodFound, Is.EqualTo(weaponFound));
                    return weaponFound;
                }
                finally
                {
                    maps.DeleteMap(mapId);
                }
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EmbeddedCrashAddsRockAndAnOpenablePatchToTheSameGrid()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.EntMan;
        var maps = server.System<SharedMapSystem>();
        var loader = server.System<MapLoaderSystem>();
        var derelicts = server.System<DerelictGenerationSystem>();
        var solutions = server.System<SharedSolutionContainerSystem>();

        await server.WaitAssertion(() =>
        {
            var vessel = server.ProtoMan.Index(SalvageVessel);
            maps.CreateMap(out var mapId);
            try
            {
                Assert.That(loader.TryLoadGrid(mapId, vessel.ShuttlePath, out var loadedGrid), Is.True);
                var grid = loadedGrid!.Value;
                var initialTiles = maps.GetAllTiles(grid.Owner, grid.Comp).Select(tile => tile.GridIndices).ToHashSet();
                Assert.That(derelicts.TryGenerate(grid, vessel, 5), Is.True);
                var addedTiles = maps.GetAllTiles(grid.Owner, grid.Comp).Select(tile => tile.GridIndices).Except(initialTiles).ToHashSet();
                Assert.That(addedTiles, Has.Count.EqualTo(5));

                var scenePrototypes = new List<string>();
                var children = entities.GetComponent<TransformComponent>(grid.Owner).ChildEnumerator;
                while (children.MoveNext(out var child))
                {
                    if (entities.GetComponent<MetaDataComponent>(child).EntityPrototype is not { } prototype)
                        continue;

                    scenePrototypes.Add(prototype.ID);
                    if (prototype.ID == "AsteroidRock")
                        Assert.That(addedTiles, Does.Contain(maps.TileIndicesFor(grid, entities.GetComponent<TransformComponent>(child).Coordinates)));
                    if (prototype.ID == "NFInflatableDoor")
                        Assert.That(entities.HasComponent<DoorComponent>(child), Is.True);
                    if (prototype.ID == "Girder")
                        Assert.That(entities.GetComponent<TransformComponent>(child).Anchored, Is.False);
                    if (prototype.ID == "NFFireExtinguisherEmpty")
                    {
                        Assert.That(solutions.TryGetSolution(child, "spray", out _, out var spray), Is.True);
                        Assert.That(spray!.Volume, Is.EqualTo(FixedPoint2.Zero));
                        Assert.That(spray.MaxVolume, Is.EqualTo(FixedPoint2.New(100)));
                    }
                    Assert.That(entities.HasComponent<NFSalvageMobRestrictionsComponent>(child), Is.False);
                }

                Assert.That(scenePrototypes.Count(prototype => prototype == "AsteroidRock"), Is.EqualTo(3));
                Assert.That(scenePrototypes, Does.Contain("NFInflatableDoor"));
                Assert.That(scenePrototypes, Does.Contain("NFInflatableWall"));
                Assert.That(scenePrototypes, Does.Contain("FoodSnackNutribrick"));
                Assert.That(scenePrototypes, Does.Contain("NFFireExtinguisherEmpty"));
                Assert.That(scenePrototypes, Does.Contain("ScrapFirelock1"));
                Assert.That(scenePrototypes, Does.Contain("Girder"));
                var damagedTile = server.ResolveDependency<ITileDefinitionManager>()["PlatingDamaged"].TileId;
                Assert.That(maps.GetAllTiles(grid.Owner, grid.Comp).Count(tile => tile.Tile.TypeId == damagedTile), Is.GreaterThanOrEqualTo(2));
            }
            finally
            {
                maps.DeleteMap(mapId);
            }
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("Vox", true)]
    [TestCase("Human", false)]
    public async Task DerelictAtmosphereMatchesFormerOwner(string species, bool expectsNitrogen)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.EntMan;
        var maps = server.System<SharedMapSystem>();
        var loader = server.System<MapLoaderSystem>();
        var derelicts = server.System<DerelictGenerationSystem>();
        var atmosphere = server.System<AtmosphereSystem>();
        var random = server.ResolveDependency<IRobustRandom>();

        await server.WaitAssertion(() =>
        {
            var vessel = server.ProtoMan.Index(ScienceVessel);
            var blacklist = server.ProtoMan.Index(ServiceCasualty).SpeciesBlacklist;
            var speciesPool = server.ProtoMan.EnumeratePrototypes<SpeciesPrototype>()
                .Where(candidate => candidate.RoundStart && !blacklist.Contains(candidate.ID)).Select(candidate => candidate.ID).ToList();
            var speciesIndex = speciesPool.IndexOf(species);
            Assert.That(speciesIndex, Is.GreaterThanOrEqualTo(0));
            var speciesSeed = Enumerable.Range(0, 1000).First(seed => new System.Random(seed).Next(speciesPool.Count) == speciesIndex);
            maps.CreateMap(out var mapId);
            try
            {
                Assert.That(loader.TryLoadGrid(mapId, vessel.ShuttlePath, out var loadedGrid), Is.True);
                var grid = loadedGrid!.Value;
                var roomTile = maps.GetAllTiles(grid.Owner, grid.Comp).Select(tile => tile.GridIndices)
                    .First(tile => atmosphere.GetTileMixture(grid.Owner, null, tile) is { Immutable: false } air && air.GetMoles(Gas.Oxygen) > 0);
                var originalAir = atmosphere.GetTileMixture(grid.Owner, null, roomTile)!;
                var originalPressure = originalAir.Pressure;
                var originalOxygen = originalAir.GetMoles(Gas.Oxygen);
                random.SetSeed(speciesSeed);
                Assert.That(derelicts.TryGenerate(grid, vessel, 5), Is.True);

                var children = entities.GetComponent<TransformComponent>(grid.Owner).ChildEnumerator;
                string? ownerSpecies = null;
                while (children.MoveNext(out var child))
                {
                    if (entities.HasComponent<MedicalBountyComponent>(child))
                        ownerSpecies = entities.GetComponent<HumanoidAppearanceComponent>(child).Species.Id;
                }

                Assert.That(ownerSpecies, Is.EqualTo(species));
                var roomAir = atmosphere.GetTileMixture(grid.Owner, null, roomTile)!;
                Assert.That(roomAir.Pressure, Is.EqualTo(originalPressure).Within(0.01f));
                Assert.That(roomAir.GetMoles(Gas.Oxygen), Is.EqualTo(expectsNitrogen ? 0 : originalOxygen));
                if (expectsNitrogen)
                    Assert.That(roomAir.GetMoles(Gas.Nitrogen), Is.EqualTo(roomAir.TotalMoles));
            }
            finally
            {
                maps.DeleteMap(mapId);
            }
        });
        await pair.CleanReturnAsync();
    }

    private static void AssertScienceCache(IEntityManager entities, EntityUid grid)
    {
        var prototypes = new List<string>();
        var children = entities.GetComponent<TransformComponent>(grid).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (entities.GetComponent<MetaDataComponent>(child).EntityPrototype is not { } prototype)
                continue;

            prototypes.Add(prototype.ID);
            if (prototype.ID == "NFDerelictAnomaly")
            {
                var anomaly = entities.GetComponent<AnomalyComponent>(child);
                Assert.That(anomaly.Stability, Is.InRange(anomaly.DecayThreshold, anomaly.GrowthThreshold));
                Assert.That(anomaly.CurrentBehavior?.Id, Is.EqualTo("Balanced"));
            }
        }

        Assert.That(prototypes, Does.Contain("NFDerelictAnomaly"));
        Assert.That(prototypes, Does.Contain("ArtifactFragment1"));
        Assert.That(prototypes, Does.Contain("SolarPanelBroken"));
    }
}
