using Content.Server._NF.Medical.Components;
using Content.Server._NF.Salvage;
using Content.Server._NF.Shipyard.Systems;
using Content.Server._NF.StationEvents.Prototypes;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Decals;
using Content.Server.Destructible;
using Content.Server.Humanoid.Systems;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared.Atmos;
using Content.Shared.Body.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Clothing.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.EntityTable;
using Content.Shared.FixedPoint;
using Content.Shared.Fluids;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;
using Content.Shared.Item;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Content.Shared.Maps;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Physics;
using Content.Shared.Storage;
using Content.Shared.Storage.Components;
using Content.Shared.Tag;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Prototypes;
using System.Linq;

namespace Content.Server._NF.StationEvents.Events;

public sealed class DerelictGenerationSystem : EntitySystem
{
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly AtmosphereSystem _atmosphere = default!;
    [Dependency] private readonly BarotraumaSystem _barotrauma = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly DecalSystem _decals = default!;
    [Dependency] private readonly DestructibleSystem _destructible = default!;
    [Dependency] private readonly SharedPuddleSystem _puddles = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private readonly EntityTableSystem _loot = default!;
    [Dependency] private readonly ShipyardSystem _shipyard = default!;
    [Dependency] private readonly RandomHumanoidSystem _humanoids = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedPoweredLightSystem _lights = default!;
    [Dependency] private readonly MobStateSystem _mobStates = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly TagSystem _tags = default!;
    [Dependency] private readonly TurfSystem _turfs = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefinitions = default!;
    [Dependency] private readonly TileSystem _tiles = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private static readonly ProtoId<TagPrototype> WallTag = "Wall";
    private static readonly EntProtoId InflatableDoor = "NFInflatableDoor";
    private static readonly EntProtoId InflatableWall = "NFInflatableWall";
    private static readonly ProtoId<EntityTablePrototype> ScienceLoot = "NFDerelictLootScience";
    private static readonly ProtoId<DamageTypePrototype> BluntDamage = "Blunt";
    private static readonly ProtoId<ShuttleAtmospherePrototype> NitrogenAtmosphere = "PureNitrogen";
    private static readonly string[] ScienceAnchors =
        ["MachineAnomalyVessel", "MachineAnomalyVesselExperimental", "MachineMiniAnomalyGenerator", "MachineAnomalyGenerator", "MachineArtifactAnalyzer"];
    private static readonly string[] SolarPanels = ["SolarPanel", "SolarPanelPlasma", "SolarPanelUranium"];
    private static readonly string[] WearDecals = ["Rust", "DirtHeavyMonotile", "Damaged"];
    private const string CrashTerrainTile = "FloorAsteroidSand";
    private const string DamagedPlatingTile = "PlatingDamaged";
    private static readonly Vector2i[] CardinalOffsets = [new(0, 1), new(1, 0), new(0, -1), new(-1, 0)];
    private const int SceneRadius = 4;
    private const int AnchorRadius = 8;
    private const int MaxAftermathEntities = 12;

    public bool TryGenerate(Entity<MapGridComponent> grid, VesselPrototype vessel, int seed)
    {
        var hasEvaKit = FindEvaKits(grid.Owner).Any();
        var floorTiles = new HashSet<Vector2i>();
        var occupiedTiles = new HashSet<Vector2i>();
        var blockedTiles = new HashSet<Vector2i>();
        var walls = new Dictionary<Vector2i, EntityUid>();
        var equipmentByPrototype = new Dictionary<string, List<Equipment>>();
        var looseItems = new List<Equipment>();
        var lightFixtures = new List<Equipment>();

        var gridChildren = Transform(grid.Owner).ChildEnumerator;
        while (gridChildren.MoveNext(out var entity))
        {
            if (MetaData(entity).EntityPrototype is not { } prototype)
                continue;

            var transform = Transform(entity);
            var entry = new Equipment(entity, _map.TileIndicesFor(grid, transform.Coordinates));
            if (!transform.Anchored && HasComp<ItemComponent>(entity))
                looseItems.Add(entry);
            if (transform.Anchored && HasComp<PoweredLightComponent>(entity))
                lightFixtures.Add(entry);

            if (!equipmentByPrototype.TryGetValue(prototype.ID, out var equipment))
                equipmentByPrototype.Add(prototype.ID, equipment = new());

            equipment.Add(entry);
        }

        foreach (var tile in _map.GetAllTiles(grid.Owner, grid.Comp))
        {
            occupiedTiles.Add(tile.GridIndices);
            if (_turfs.IsSpace(tile))
                continue;

            var position = tile.GridIndices;
            floorTiles.Add(position);
            EntityUid? wall = null;
            var blockerCount = 0;
            foreach (var entity in _map.GetAnchoredEntities(grid, position))
            {
                if (HasComp<AirtightComponent>(entity) || BlocksMovement(entity))
                    blockerCount++;

                if (HasComp<AirtightComponent>(entity) && _tags.HasTag(entity, WallTag))
                    wall = entity;
            }

            if (blockerCount > 0)
                blockedTiles.Add(position);

            if (blockerCount == 1 && wall is { } singleWall)
                walls.Add(position, singleWall);
        }

        var breaches = new List<Incident>();
        foreach (var (position, wall) in walls.OrderBy(entry => entry.Key.X).ThenBy(entry => entry.Key.Y))
        {
            foreach (var outward in CardinalOffsets)
            {
                var inside = position - outward;
                if (floorTiles.Contains(position + outward) || !floorTiles.Contains(inside) || blockedTiles.Contains(inside))
                    continue;

                var sceneTiles = FindReachableTiles(inside, floorTiles, blockedTiles);
                if (sceneTiles.Count > 0)
                    breaches.Add(new Incident(position, wall, outward, sceneTiles));
            }
        }

        var candidates = new List<(DerelictThemePrototype Theme, List<Incident> Incidents)>();
        long totalWeight = 0;
        foreach (var theme in _prototypes.EnumeratePrototypes<DerelictThemePrototype>().OrderBy(theme => theme.ID, StringComparer.Ordinal))
        {
            if (theme.Weight <= 0 || theme.Classes.Count > 0 && !theme.Classes.Any(vessel.Classes.Contains))
                continue;

            var threatCount = theme.Threats.Count > 0 ? Math.Max(0, theme.ThreatCount) : 0;
            var requiredSpace = theme.Aftermath.Count + (theme.Casualty.HasValue ? 1 : 0) + threatCount;
            if (requiredSpace > MaxAftermathEntities)
                continue;

            var equipment = theme.Anchors
                .Distinct()
                .Where(anchor => equipmentByPrototype.ContainsKey(anchor.Id))
                .SelectMany(anchor => equipmentByPrototype[anchor.Id])
                .OrderBy(anchor => anchor.Position.X)
                .ThenBy(anchor => anchor.Position.Y)
                .ToList();
            var incidents = theme.EquipmentDamage == null
                ? breaches.Where(breach => theme.Anchors.Count == 0 ||
                    equipment.Any(anchor => TileDistance(breach.Position, anchor.Position) <= AnchorRadius))
                : FindEquipmentIncidents(equipment, floorTiles, blockedTiles);
            var eligibleIncidents = new List<Incident>();
            foreach (var incident in incidents)
            {
                if (incident.SceneTiles.Count < requiredSpace)
                    continue;

                if (theme.Casualty.HasValue && !hasEvaKit &&
                    (incident.Outward.HasValue || !incident.SceneTiles.Any(tile => !IsLowPressure(grid, tile))))
                {
                    continue;
                }

                var crash = theme.CrashRock.HasValue ? FindCrashPlacement(incident, walls, occupiedTiles, theme.LargeImpact) : null;
                if (theme.CrashRock.HasValue && crash == null)
                    continue;

                Equipment? wreckedEquipment = null;
                if (theme.EquipmentWreck.HasValue)
                {
                    if (equipment.Count == 0)
                        continue;

                    wreckedEquipment = equipment.MinBy(anchor => TileDistance(incident.Position, anchor.Position));
                }

                eligibleIncidents.Add(incident with { Crash = crash, WreckedEquipment = wreckedEquipment });
            }

            if (eligibleIncidents.Count == 0)
                continue;

            candidates.Add((theme, eligibleIncidents));
            totalWeight += theme.Weight;
        }

        if (totalWeight == 0)
            return false;

        var random = new Random(seed);
        var weightRoll = random.NextInt64(totalWeight);
        foreach (var (theme, eligibleIncidents) in candidates)
        {
            weightRoll -= theme.Weight;
            if (weightRoll >= 0)
                continue;

            var incident = eligibleIncidents[random.Next(eligibleIncidents.Count)];
            if (!ApplyTheme(grid, vessel, theme, incident, breaches, hasEvaKit, random, out var voxOwned))
                return false;

            ShuffleLooseItems(grid, incident, looseItems, random);
            DamageSolarPanels(incident, SolarPanels.SelectMany(id => equipmentByPrototype.GetValueOrDefault(id) ?? []));
            if (vessel.Classes.Contains(VesselClass.Science))
            {
                var labs = ScienceAnchors.SelectMany(id => equipmentByPrototype.GetValueOrDefault(id) ?? [])
                    .OrderBy(anchor => TileDistance(incident.Position, anchor.Position));
                var lab = FindEquipmentIncidents(labs, floorTiles, blockedTiles).FirstOrDefault();
                PlaceScienceCache(grid, lab ?? incident, random);
            }

            AgeWreck(grid, incident, floorTiles, blockedTiles, lightFixtures, random);
            if (voxOwned)
                FillNitrogenAtmosphere(grid, floorTiles);

            Log.Info($"Generated derelict {vessel.ID} with theme {theme.ID}, seed {seed}, incident at {incident.Position}");
            return true;
        }

        return false;
    }

    private bool BlocksMovement(EntityUid entity)
    {
        return TryComp<FixturesComponent>(entity, out var fixtures) &&
               fixtures.Fixtures.Values.Any(fixture => fixture.Hard && (fixture.CollisionLayer & (int) CollisionGroup.MobMask) != 0);
    }

    private static List<Vector2i> FindReachableTiles(
        Vector2i start,
        HashSet<Vector2i> floorTiles,
        HashSet<Vector2i> blockedTiles)
    {
        var reachable = new List<Vector2i> { start };
        var seen = new HashSet<Vector2i> { start };
        for (var tileIndex = 0; tileIndex < reachable.Count; tileIndex++)
        {
            foreach (var offset in CardinalOffsets)
            {
                var neighbor = reachable[tileIndex] + offset;
                if (TileDistance(neighbor, start) > SceneRadius || !floorTiles.Contains(neighbor) ||
                    blockedTiles.Contains(neighbor) || !seen.Add(neighbor))
                {
                    continue;
                }

                reachable.Add(neighbor);
            }
        }

        return reachable;
    }

    private IEnumerable<Incident> FindEquipmentIncidents(
        IEnumerable<Equipment> equipment,
        HashSet<Vector2i> floorTiles,
        HashSet<Vector2i> blockedTiles)
    {
        foreach (var anchor in equipment)
        {
            if (!floorTiles.Contains(anchor.Position) || !HasComp<DamageableComponent>(anchor.Entity))
                continue;

            foreach (var offset in CardinalOffsets)
            {
                var adjacentTile = anchor.Position + offset;
                if (!floorTiles.Contains(adjacentTile) || blockedTiles.Contains(adjacentTile))
                    continue;

                yield return new Incident(anchor.Position, anchor.Entity, null,
                    FindReachableTiles(adjacentTile, floorTiles, blockedTiles));
            }
        }
    }

    private static int TileDistance(Vector2i first, Vector2i second)
    {
        return Math.Abs(first.X - second.X) + Math.Abs(first.Y - second.Y);
    }

    private static CrashPlacement? FindCrashPlacement(
        Incident incident,
        Dictionary<Vector2i, EntityUid> walls,
        HashSet<Vector2i> occupiedTiles,
        bool largeImpact)
    {
        if (incident.Outward is not { } outward || occupiedTiles.Contains(incident.Position + outward))
            return null;

        foreach (var side in CardinalOffsets)
        {
            if (side == outward || side == -outward)
                continue;

            var flank = incident.Position + side;
            if (!walls.TryGetValue(flank, out var flankWall))
                continue;

            var firstRockTile = flank + outward;
            var outerRockTile = firstRockTile + outward;
            Vector2i[] footprint = [firstRockTile, outerRockTile, firstRockTile + side, outerRockTile + side, outerRockTile + outward];
            if (largeImpact)
                footprint = [.. footprint, outerRockTile + outward + side, outerRockTile + side * 2, firstRockTile + side * 2];

            if (footprint.All(tile => !occupiedTiles.Contains(tile)))
                return new CrashPlacement(flank, flankWall, footprint);
        }

        return null;
    }

    private bool ApplyTheme(
        Entity<MapGridComponent> grid,
        VesselPrototype vessel,
        DerelictThemePrototype theme,
        Incident incident,
        List<Incident> breaches,
        bool hasEvaKit,
        Random random,
        out bool voxOwned)
    {
        voxOwned = false;
        if (theme.EquipmentDamage is { } damage)
        {
            _damageable.TryChangeDamage(incident.Target, damage, ignoreResistances: true);
        }
        else
        {
            // Keep the plating under the walls so damage cannot split the grid.
            Del(incident.Target);
            if (incident.Crash is { } crash && theme.CrashRock is { } rock)
            {
                Del(crash.FlankWall);
                var terrain = (ContentTileDefinition) _tileDefinitions[CrashTerrainTile];
                _map.SetTiles(grid.Owner, grid.Comp, crash.Footprint
                    .Select(tile => (tile, _tiles.GetVariantTile(terrain, random))).ToList());
                foreach (var tile in crash.Footprint.Take(theme.LargeImpact ? 5 : 3))
                    Spawn(rock, TileCenter(grid, tile));

                if (theme.ImpactGrowth is { } growth)
                {
                    Spawn(growth, TileCenter(grid, crash.Flank));
                    Spawn(growth, TileCenter(grid, crash.Footprint[^1]));
                }
            }
            else if (vessel.Category >= VesselSize.Medium && theme.MaxBreachWidth >= 2 && random.Next(2) == 0)
            {
                var neighboringBreaches = breaches.Where(other => other.Outward == incident.Outward &&
                    TileDistance(other.Position, incident.Position) == 1).ToList();
                if (neighboringBreaches.Count > 0)
                    Del(neighboringBreaches[random.Next(neighboringBreaches.Count)].Target);
            }
        }

        if (theme.ImpactTrail && incident.Outward is { } outward)
            PlaceImpactTrail(grid, incident, outward, random);

        if (incident.WreckedEquipment is { } wreckedEquipment && theme.EquipmentWreck is { } replacement)
        {
            Del(wreckedEquipment.Entity);
            Spawn(replacement, _map.GridTileToLocal(grid.Owner, grid.Comp, wreckedEquipment.Position));
        }

        var sceneTiles = incident.SceneTiles.ToArray();
        random.Shuffle(sceneTiles.AsSpan());
        if (theme.ScorchedTiles > 0)
        {
            foreach (var tile in incident.SceneTiles.Take(Math.Min(theme.ScorchedTiles, MaxAftermathEntities)))
            {
                var coordinates = new EntityCoordinates(grid.Owner, tile * grid.Comp.TileSize);
                _decals.TryAddDecal("burnt1", coordinates, out _, cleanable: true);
            }
        }

        var placementIndex = 0;
        var placementLimit = Math.Min(sceneTiles.Length, MaxAftermathEntities);
        if (theme.Casualty is { } casualtySettings)
        {
            if (!hasEvaKit)
            {
                // Only pressurized casualty tiles were eligible on an unstocked wreck.
                var safeIndex = Array.FindIndex(sceneTiles, tile => !IsLowPressure(grid, tile));
                if (safeIndex < 0)
                    return false;

                (sceneTiles[0], sceneTiles[safeIndex]) = (sceneTiles[safeIndex], sceneTiles[0]);
            }

            if (!TrySpawnCasualty(grid, theme, incident, sceneTiles[placementIndex++], casualtySettings.Id, out var body))
                return false;

            voxOwned = TryComp<HumanoidAppearanceComponent>(body, out var appearance) && appearance.Species.Id == "Vox";
        }

        foreach (var prototype in theme.Aftermath)
            Spawn(prototype, NextPlacement());

        EntityUid? firstThreat = null;
        for (var threatIndex = 0; threatIndex < theme.ThreatCount && theme.Threats.Count > 0; threatIndex++)
        {
            var threat = Spawn(theme.Threats[random.Next(theme.Threats.Count)], NextPlacement());
            EnsureComp<NFSalvageMobRestrictionsComponent>(threat);
            firstThreat ??= threat;
        }

        if (theme.Casualty.HasValue && firstThreat is { } opponent && theme.DefenseWeapon is { } weapon &&
            placementIndex < placementLimit && random.NextDouble() < 0.65)
        {
            var weaponIndex = placementIndex;
            for (var candidateIndex = placementIndex + 1; candidateIndex < placementLimit; candidateIndex++)
            {
                if (TileDistance(sceneTiles[candidateIndex], sceneTiles[0]) < TileDistance(sceneTiles[weaponIndex], sceneTiles[0]))
                    weaponIndex = candidateIndex;
            }

            (sceneTiles[placementIndex], sceneTiles[weaponIndex]) = (sceneTiles[weaponIndex], sceneTiles[placementIndex]);
            Spawn(weapon, NextPlacement());
            if (TryComp<BloodstreamComponent>(opponent, out var bloodstream))
                SpillBlood(opponent, bloodstream, Transform(opponent).Coordinates, 12);
        }

        if (!vessel.Classes.Contains(VesselClass.Science) && theme.LootTable is { } lootTable)
        {
            var availableLootTiles = placementLimit - placementIndex;
            foreach (var loot in _loot.GetSpawns(_prototypes.Index(lootTable), random).Take(availableLootTiles))
                Spawn(loot, NextPlacement());
        }

        // Keep the main route open until the emergency door is placed. The door can be opened or deflated.
        if (incident.Outward.HasValue &&
            theme.DamageControlChance > 0f && random.NextDouble() < theme.DamageControlChance)
        {
            Spawn(InflatableDoor, TileCenter(grid, incident.Position));
            if (incident.Crash is { } crash)
                Spawn(InflatableWall, TileCenter(grid, crash.Flank));
        }

        if (theme.Casualty is not { } additionalSettings || theme.AdditionalCasualtyChance <= 0f ||
            placementIndex >= placementLimit || random.NextDouble() >= theme.AdditionalCasualtyChance)
        {
            return true;
        }

        // An exposed crewmember needs a separate stocked locker. Search again after the primary casualty was equipped.
        var hasSpareKit = FindEvaKits(grid.Owner).Any();
        var crewTile = sceneTiles[placementIndex..placementLimit]
            .Where(tile => hasSpareKit || !RequiresEva(grid, incident, tile))
            .OrderByDescending(tile => TileDistance(tile, sceneTiles[0]))
            .Select(tile => (Vector2i?) tile)
            .FirstOrDefault();
        return crewTile is not { } secondTile || TrySpawnCasualty(grid, theme, incident, secondTile, additionalSettings.Id, out _);

        EntityCoordinates NextPlacement()
        {
            return TileCenter(grid, sceneTiles[placementIndex++]);
        }
    }

    private bool TrySpawnCasualty(Entity<MapGridComponent> grid, DerelictThemePrototype theme, Incident incident,
        Vector2i tile, string settingsId, out EntityUid body)
    {
        body = _humanoids.SpawnRandomHumanoid(settingsId, TileCenter(grid, tile), string.Empty, humanoid =>
        {
            if (theme.CasualtyBounty is { } bountyId)
                Comp<MedicalBountyComponent>(humanoid).Bounty = _prototypes.Index(bountyId);
        });

        if (RequiresEva(grid, incident, tile) && !TryEquipCasualtySuit(grid.Owner, body))
        {
            Del(body);
            return false;
        }

        _mobStates.ChangeMobState(body, MobState.Dead);
        var bloodloss = Comp<DamageableComponent>(body).Damage.DamageDict.GetValueOrDefault("Bloodloss");
        SpillCasualtyBlood(grid, body, bloodloss, incident.SceneTiles, tile);
        return true;
    }

    private bool RequiresEva(Entity<MapGridComponent> grid, Incident incident, Vector2i tile)
    {
        // The breach will vent on later atmosphere updates, so its current pressure can still look safe.
        return incident.Outward.HasValue || IsLowPressure(grid, tile);
    }

    private EntityCoordinates TileCenter(Entity<MapGridComponent> grid, Vector2i tile)
    {
        return _map.GridTileToLocal(grid.Owner, grid.Comp, tile);
    }

    private void AgeWreck(Entity<MapGridComponent> grid, Incident incident, HashSet<Vector2i> floorTiles,
        HashSet<Vector2i> blockedTiles, List<Equipment> lightFixtures, Random random)
    {
        var walkable = floorTiles.Where(tile => !blockedTiles.Contains(tile))
            .OrderBy(tile => tile.X).ThenBy(tile => tile.Y).ToArray();
        random.Shuffle(walkable.AsSpan());
        var wearCount = Math.Clamp(walkable.Length / 3, 6, 48);
        var damagedPlating = (ContentTileDefinition) _tileDefinitions[DamagedPlatingTile];
        var damagedTiles = new List<(Vector2i, Tile)>();
        foreach (var (wearIndex, tile) in walkable.OrderBy(incident.SceneTiles.Contains).Take(wearCount).Index())
        {
            var decal = WearDecals[wearIndex % WearDecals.Length];
            _decals.TryAddDecal(decal, new EntityCoordinates(grid.Owner, tile * grid.Comp.TileSize), out _, cleanable: true);

            if (decal != "Damaged")
                continue;

            var existing = (ContentTileDefinition) _tileDefinitions[_map.GetTileRef(grid.Owner, grid.Comp, tile).Tile.TypeId];
            // Leave special surfaces and existing pressure boundaries intact.
            if (existing.ID == "Plating" || existing.BaseTurf == "Plating")
                damagedTiles.Add((tile, _tiles.GetVariantTile(damagedPlating, random)));
        }

        if (damagedTiles.Count > 0)
            _map.SetTiles(grid.Owner, grid.Comp, damagedTiles);

        var fixtures = lightFixtures.OrderBy(fixture => fixture.Position.X).ThenBy(fixture => fixture.Position.Y).ToArray();
        random.Shuffle(fixtures.AsSpan());
        foreach (var fixture in fixtures.Take(Math.Min(fixtures.Length / 2, 16)))
            _lights.TryDestroyBulb(fixture.Entity);
    }

    private void PlaceImpactTrail(Entity<MapGridComponent> grid, Incident incident, Vector2i outward, Random random)
    {
        var damagedPlating = (ContentTileDefinition) _tileDefinitions[DamagedPlatingTile];
        for (var distance = 0; distance <= SceneRadius; distance++)
        {
            var tile = incident.Position - outward * distance;
            if (distance > 0 && !incident.SceneTiles.Contains(tile))
                break;

            _map.SetTile(grid.Owner, grid.Comp, tile, _tiles.GetVariantTile(damagedPlating, random));
            var decalCoordinates = new EntityCoordinates(grid.Owner, tile * grid.Comp.TileSize);
            _decals.TryAddDecal($"burnt{random.Next(1, 5)}", decalCoordinates, out _, cleanable: true);

            if (distance == 1)
                _transform.Unanchor(Spawn("Girder", TileCenter(grid, tile)));
        }
    }

    private void SpillCasualtyBlood(Entity<MapGridComponent> grid, EntityUid body, FixedPoint2 bloodloss,
        List<Vector2i> sceneTiles, Vector2i bodyTile)
    {
        if (bloodloss <= FixedPoint2.Zero || !TryComp<BloodstreamComponent>(body, out var bloodstream))
            return;

        // Bounty damage is measured in points. Use a unit per point up to a species-relative limit.
        var volume = FixedPoint2.Min(bloodloss, bloodstream.BloodMaxVolume * 0.7f);
        var trail = sceneTiles.Where(tile => tile != bodyTile)
            .OrderBy(tile => TileDistance(tile, bodyTile)).Take(2).ToArray();

        SpillBlood(body, bloodstream, TileCenter(grid, bodyTile), volume * (1f - trail.Length * 0.2f));
        foreach (var tile in trail)
            SpillBlood(body, bloodstream, TileCenter(grid, tile), volume * 0.2f);
    }

    private void SpillBlood(EntityUid source, BloodstreamComponent bloodstream, EntityCoordinates coordinates, FixedPoint2 volume)
    {
        if (!_solutions.TryGetSolution(source, bloodstream.BloodSolutionName, out var blood, out var solution) || solution.Volume <= 0)
            return;

        var spilled = _solutions.SplitSolution(blood.Value, volume);
        _puddles.TrySpillAt(coordinates, spilled, out _, sound: false);
    }

    private void ShuffleLooseItems(Entity<MapGridComponent> grid, Incident incident, List<Equipment> items, Random random)
    {
        // Move loose floor stock within the incident's reachable area. Leave installed equipment and containers alone.
        var candidates = items.Where(item => incident.SceneTiles.Contains(item.Position)).ToArray();
        random.Shuffle(candidates.AsSpan());
        foreach (var item in candidates.Take(3))
        {
            var destinations = incident.SceneTiles.Where(tile => tile != item.Position && TileDistance(tile, item.Position) <= 2).ToArray();
            if (destinations.Length > 0)
                _transform.SetCoordinates(item.Entity, TileCenter(grid, destinations[random.Next(destinations.Length)]));
        }
    }

    private void DamageSolarPanels(Incident incident, IEnumerable<Equipment> panels)
    {
        var blunt = _prototypes.Index(BluntDamage);
        foreach (var panel in panels.OrderBy(panel => TileDistance(incident.Position, panel.Position)).Take(3))
        {
            var breakAt = _destructible.DestroyedAt(panel.Entity);
            if (!TryComp<DamageableComponent>(panel.Entity, out var damageable) ||
                breakAt == FixedPoint2.MaxValue || damageable.TotalDamage >= breakAt)
                continue;

            var damage = new DamageSpecifier(blunt, breakAt - damageable.TotalDamage);
            _damageable.TryChangeDamage(panel.Entity, damage, ignoreResistances: true);
        }
    }

    private void PlaceScienceCache(Entity<MapGridComponent> grid, Incident lab, Random random)
    {
        var tiles = lab.SceneTiles.ToArray();
        random.Shuffle(tiles.AsSpan());
        Spawn("NFDerelictAnomaly", TileCenter(grid, tiles[0]));
        Spawn("ArtifactFragment1", TileCenter(grid, tiles[Math.Min(1, tiles.Length - 1)]));
        var lootIndex = 2;
        foreach (var loot in _loot.GetSpawns(_prototypes.Index(ScienceLoot), random))
            Spawn(loot, TileCenter(grid, tiles[Math.Min(lootIndex++, tiles.Length - 1)]));
    }

    private void FillNitrogenAtmosphere(Entity<MapGridComponent> grid, HashSet<Vector2i> floorTiles)
    {
        // The casualty is the former owner. Keep the original pressure and temperature, including any existing vacuum.
        foreach (var tile in floorTiles)
        {
            if (_atmosphere.GetTileMixture(grid.Owner, null, tile, excite: true) is not { Immutable: false } air)
                continue;

            var moles = air.TotalMoles;
            air.Clear();
            air.SetMoles(Gas.Nitrogen, moles);
        }

        _shipyard.ConfigureShuttleAtmosphereDevices(grid.Owner, _prototypes.Index(NitrogenAtmosphere));
    }

    private bool IsLowPressure(Entity<MapGridComponent> grid, Vector2i tile)
    {
        var mixture = _atmosphere.GetTileMixture(grid.Owner, Transform(grid.Owner).MapUid, tile);
        return (mixture?.Pressure ?? 0f) <= Atmospherics.HazardLowPressure;
    }

    private bool TryEquipCasualtySuit(EntityUid grid, EntityUid casualty)
    {
        // The damage pass can change the locker stock, so choose from the remaining kits.
        var evaKits = FindEvaKits(grid).ToList();
        if (evaKits.Count == 0)
            return false;

        var position = Transform(casualty).LocalPosition;
        var kit = evaKits.MinBy(candidate => (Transform(candidate.Locker.Owner).LocalPosition - position).LengthSquared());

        // These garments are generated with the casualty and can be replaced by the EVA loadout.
        if (_inventory.TryGetSlotEntity(casualty, "head", out var headwear))
            Del(headwear.Value);

        if (_inventory.TryGetSlotEntity(casualty, "outerClothing", out var outerwear))
            Del(outerwear.Value);

        if (!_inventory.TryEquip(casualty, kit.Suit, "outerClothing", silent: true, force: true) ||
            !_inventory.TryEquip(casualty, kit.Helmet, "head", silent: true, force: true))
        {
            return false;
        }

        // Barotrauma clamps ambient pressure to 1 kPa before checking equipment.
        if (TryComp<BarotraumaComponent>(casualty, out var barotrauma) &&
            _barotrauma.GetFeltLowPressure(casualty, barotrauma, 1f) <= Atmospherics.HazardLowPressure)
        {
            return false;
        }

        foreach (var item in kit.Locker.ContainedEntities.ToArray())
            Del(item);

        return true;
    }

    private IEnumerable<EvaKit> FindEvaKits(EntityUid grid)
    {
        var gridChildren = Transform(grid).ChildEnumerator;
        while (gridChildren.MoveNext(out var child))
        {
            if (!Transform(child).Anchored)
                continue;

            if (TryComp<EntityStorageComponent>(child, out var closet))
            {
                foreach (var kit in FindLockerEvaKits(closet.Contents))
                    yield return kit;
            }
            if (TryComp<StorageComponent>(child, out var locker))
            {
                foreach (var kit in FindLockerEvaKits(locker.Container))
                    yield return kit;
            }
        }
    }

    private IEnumerable<EvaKit> FindLockerEvaKits(BaseContainer locker)
    {
        foreach (var suit in locker.ContainedEntities)
        {
            if (!IsClothingForSlot(suit, SlotFlags.OUTERCLOTHING) ||
                !_barotrauma.TryGetPressureProtectionValues(suit, out _, out _, out var suitMultiplier, out var suitModifier))
            {
                continue;
            }

            var helmet = TryComp<ToggleableClothingComponent>(suit, out var toggleable) ? toggleable.ClothingUid : null;
            if (helmet is { } attachedHelmet)
            {
                if (CanSeal(attachedHelmet, suitMultiplier.Value, suitModifier.Value))
                    yield return new EvaKit(locker, suit, attachedHelmet);
                continue;
            }

            foreach (var separateHelmet in locker.ContainedEntities)
            {
                if (!CanSeal(separateHelmet, suitMultiplier.Value, suitModifier.Value))
                    continue;

                yield return new EvaKit(locker, suit, separateHelmet);
                break;
            }
        }
    }

    private bool CanSeal(EntityUid helmet, float suitMultiplier, float suitModifier)
    {
        // Barotrauma uses the weakest multiplier and modifier from the equipped items.
        return IsClothingForSlot(helmet, SlotFlags.HEAD) &&
            _barotrauma.TryGetPressureProtectionValues(helmet, out _, out _, out var multiplier, out var modifier) &&
            (1f + Math.Min(suitModifier, modifier.Value)) * Math.Min(suitMultiplier, multiplier.Value) >
            Atmospherics.HazardLowPressure;
    }

    private bool IsClothingForSlot(EntityUid item, SlotFlags slot)
    {
        return TryComp<ClothingComponent>(item, out var clothing) && (clothing.Slots & slot) != 0;
    }

    private sealed record Incident(
        Vector2i Position,
        EntityUid Target,
        Vector2i? Outward,
        List<Vector2i> SceneTiles,
        CrashPlacement? Crash = null,
        Equipment? WreckedEquipment = null);
    private sealed record CrashPlacement(Vector2i Flank, EntityUid FlankWall, Vector2i[] Footprint);
    private readonly record struct Equipment(EntityUid Entity, Vector2i Position);
    private readonly record struct EvaKit(BaseContainer Locker, EntityUid Suit, EntityUid Helmet);
}
