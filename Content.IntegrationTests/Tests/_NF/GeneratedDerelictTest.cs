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
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared.Atmos;
using Content.Shared.Body.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Clothing.Components;
using Content.Shared.Damage;
using Content.Shared.Doors.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Fluids.Components;
using Content.Shared.Inventory;
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

namespace Content.IntegrationTests.Tests._NF;

[TestFixture]
public sealed class GeneratedDerelictTest
{
    private static readonly ProtoId<VesselPrototype> SalvageVessel = "Bodkin";
    private static readonly ProtoId<VesselPrototype> ScienceVessel = "Investigator";

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
            var rule = ticker.AddGameRule("BluespaceGeneratedSalvage");
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

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExposedSceneRequiresAStockedKitAndAcceptsASeparateHelmet(bool stockEva)
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
            maps.CreateMap(out var mapId);
            try
            {
                Assert.That(loader.TryLoadGrid(mapId, vessel.ShuttlePath, out var loadedGrid), Is.True);
                var grid = loadedGrid!.Value;
                var transform = entities.GetComponent<TransformComponent>(grid.Owner);
                var children = transform.ChildEnumerator;
                BaseContainer? testLocker = null;
                while (children.MoveNext(out var child))
                {
                    if (entities.TryGetComponent<EntityStorageComponent>(child, out var closet))
                    {
                        foreach (var item in closet.Contents.ContainedEntities.ToArray())
                            entities.DeleteEntity(item);
                        if (entities.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID == "SuitStorageWallmountSalv")
                            testLocker = closet.Contents;
                    }
                    if (entities.TryGetComponent<StorageComponent>(child, out var locker))
                    {
                        foreach (var item in locker.Container.ContainedEntities.ToArray())
                            entities.DeleteEntity(item);
                    }
                }

                if (!stockEva)
                {
                    Assert.That(derelicts.TryGenerate(grid, vessel, 1), Is.False);
                    return;
                }

                Assert.That(testLocker, Is.Not.Null);
                var coordinates = new EntityCoordinates(testLocker!.Owner, Vector2.Zero);
                var suit = entities.SpawnEntity("ClothingOuterHardsuitEVA", coordinates);
                var helmet = entities.SpawnEntity("ClothingHeadHelmetEVA", coordinates);
                Assert.That(containers.Insert(suit, testLocker), Is.True);
                Assert.That(containers.Insert(helmet, testLocker), Is.True);
                Assert.That(derelicts.TryGenerate(grid, vessel, 1), Is.True);

                children = transform.ChildEnumerator;
                var casualty = EntityUid.Invalid;
                while (children.MoveNext(out var child))
                {
                    if (entities.HasComponent<MedicalBountyComponent>(child))
                        casualty = child;
                }

                Assert.That(casualty, Is.Not.EqualTo(EntityUid.Invalid));
                Assert.That(inventory.TryGetSlotEntity(casualty, "outerClothing", out var equippedSuit), Is.True);
                Assert.That(inventory.TryGetSlotEntity(casualty, "head", out var equippedHelmet), Is.True);
                Assert.That(equippedSuit, Is.EqualTo(suit));
                Assert.That(equippedHelmet, Is.EqualTo(helmet));
                Assert.That(testLocker.ContainedEntities, Is.Empty);
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
            var replay = Generate(seed);
            Assert.That(scene.HasThreat, Is.EqualTo(expectThreat));
            Assert.That(replay.HasThreat, Is.EqualTo(scene.HasThreat));
            Assert.That(replay.Breaches, Is.EqualTo(scene.Breaches));
            Assert.That(replay.Casualty, Is.EqualTo(scene.Casualty));
            Assert.That(replay.Aftermath, Is.EquivalentTo(scene.Aftermath));

            (bool HasThreat, Vector2i[] Breaches, Vector2i Casualty, List<(string, Vector2i)> Aftermath) Generate(int sceneSeed)
            {
                maps.CreateMap(out var mapId);
                try
                {
                    Assert.That(loader.TryLoadGrid(mapId, vessel.ShuttlePath, out var loadedGrid), Is.True);
                    var grid = loadedGrid!.Value;
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

                        if (!initialEntities.Contains(child) && entities.GetComponent<MetaDataComponent>(child).EntityPrototype is { } prototype)
                            aftermath.Add((prototype.ID, maps.TileIndicesFor(grid, entities.GetComponent<TransformComponent>(child).Coordinates)));

                        if (entities.TryGetComponent<NFSalvageMobRestrictionsComponent>(child, out var restrictions))
                        {
                            Assert.That(restrictions.LinkedGridEntity, Is.EqualTo(grid.Owner));
                            hasThreat = true;
                        }
                    }

                    Assert.That(casualties, Has.Count.EqualTo(1));
                    Assert.That(aftermath, Has.Count.InRange(2, expectThreat ? 6 : 4));
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

    [TestCase("Garden", 9, "NFDerelictKudzu", 3, "MobTomatoKiller", true)]
    [TestCase("Bodkin", 2, "NFDerelictTendons", 3, null, true)]
    [TestCase("Bodkin", 0, "NFDerelictImpactRock", 1, "NFMobXeno", false)]
    public async Task NewThemesPlaceBoundedAftermathAndOneThreat(
        string vesselId,
        int seed,
        string expectedAftermath,
        int expectedCount,
        string? expectedThreat,
        bool expectBlood)
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
                Assert.That(derelicts.TryGenerate(grid, vessel, seed), Is.True);

                var aftermathCount = 0;
                var threats = new List<string>();
                var puddles = new List<EntityUid>();
                var casualty = EntityUid.Invalid;
                var children = entities.GetComponent<TransformComponent>(grid.Owner).ChildEnumerator;
                while (children.MoveNext(out var child))
                {
                    var prototype = entities.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID;
                    if (prototype == expectedAftermath)
                        aftermathCount++;

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
                Assert.That(threats, Has.Count.EqualTo(1));
                if (expectedThreat != null)
                    Assert.That(threats.Single(), Is.EqualTo(expectedThreat));
                else
                    Assert.That(threats.Single(), Is.AnyOf("MobFleshClampSalvage", "MobFleshJaredSalvage"));

                Assert.That(casualty, Is.Not.EqualTo(EntityUid.Invalid));
                var casualtyTile = maps.TileIndicesFor(grid, entities.GetComponent<TransformComponent>(casualty).Coordinates);
                var reagent = entities.GetComponent<BloodstreamComponent>(casualty).BloodReagent;
                var hasBlood = puddles.Any(puddle =>
                    maps.TileIndicesFor(grid, entities.GetComponent<TransformComponent>(puddle).Coordinates) == casualtyTile &&
                    solutions.TryGetSolution(puddle, entities.GetComponent<PuddleComponent>(puddle).SolutionName, out _, out var solution) &&
                    solution.Contents.Any(quantity => quantity.Reagent.Prototype == reagent.Id));
                Assert.That(hasBlood, Is.EqualTo(expectBlood));
            }
            finally
            {
                maps.DeleteMap(mapId);
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
            }
            finally
            {
                maps.DeleteMap(mapId);
            }
        });
        await pair.CleanReturnAsync();
    }
}
