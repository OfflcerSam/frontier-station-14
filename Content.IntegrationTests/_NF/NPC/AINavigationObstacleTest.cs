#nullable enable
using System.Collections.Generic;
using Content.Server.Gravity;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared.Damage;
using Content.Shared.DoAfter;
using Content.Shared.Gravity;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.NPC;
using Content.Shared.Prying.Components;
using Content.Shared.Weapons.Melee;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests._NF.NPC;

[TestFixture]
[Category("AILogicRegression")]
public sealed class AINavigationObstacleTest
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task MovingCrateRebuildsDepartedAndArrivalChunks(bool transferGrid)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var transforms = server.System<SharedTransformSystem>();
        var pathfinding = server.System<PathfindingSystem>();
        var start = map.GridCoords.Offset(new(0.5f, 0.5f));
        EntityCoordinates destination = default;
        EntityUid crate = default;

        await server.WaitPost(() =>
        {
            var maps = server.System<SharedMapSystem>();
            var tile = new Tile(server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            if (transferGrid)
            {
                var otherGrid = server.MapMan.CreateGridEntity(map.MapId);
                transforms.SetCoordinates(otherGrid.Owner, new EntityCoordinates(map.MapUid, 30, 0));
                destination = new EntityCoordinates(otherGrid.Owner, 0.5f, 0.5f);
                maps.SetTile(otherGrid.Owner, otherGrid.Comp, destination, tile);
            }
            else
            {
                destination = map.GridCoords.Offset(new(16.5f, 0.5f));
                maps.SetTile(map.Grid.Owner, map.Grid.Comp, destination, tile);
            }

            crate = server.EntMan.SpawnEntity("CrateGenericSteel", start);
        });
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            Assert.That(pathfinding.GetPoly(start)?.Data.Damage, Is.GreaterThan(0f), "The original crate must be in the navmesh.");
            Assert.That(pathfinding.GetPoly(destination)?.Data.Damage, Is.Zero);
            transforms.SetCoordinates(crate, destination);
        });
        await server.WaitRunTicks(30);

        await server.WaitAssertion(() =>
        {
            Assert.That(pathfinding.GetPoly(start)?.Data.Damage, Is.Zero, "The departed chunk must forget the crate.");
            Assert.That(pathfinding.GetPoly(destination)?.Data.Damage, Is.GreaterThan(0f), "The arrival chunk must see the crate.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ThinDoorFlagsStayWithinItsCollisionSamples()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var pathfinding = server.System<PathfindingSystem>();
        await server.WaitPost(() => server.EntMan.SpawnEntity("Windoor", map.GridCoords.Offset(new(0.5f, 0.5f))));
        await server.WaitRunTicks(30);

        await server.WaitAssertion(() =>
        {
            var covered = pathfinding.GetPoly(map.GridCoords.Offset(new(0.125f, 0.125f)));
            var clear = pathfinding.GetPoly(map.GridCoords.Offset(new(0.875f, 0.875f)));
            Assert.That(covered, Is.Not.Null);
            Assert.That(clear, Is.Not.Null);
            Assert.That(covered!.Data.Flags.HasFlag(PathfindingBreadcrumbFlag.Door), Is.True);
            Assert.That(clear!.Data.Flags, Is.EqualTo(PathfindingBreadcrumbFlag.None));
        });

        await pair.CleanReturnAsync();
    }

    [TestCase(false, 1.5f, true)]
    [TestCase(true, 1.5f, true)]
    [TestCase(true, 0.75f, true)]
    [TestCase(true, 0.75f, false)]
    public async Task CrateHandlingHonorsWeaponRangeAndCooldown(bool anchored, float weaponRange, bool clearsBlocker)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { ServerSeed = 12345 });
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var shortRange = weaponRange < 1.5f;
        var start = map.GridCoords.Offset(new(0.5f, 0.5f));
        var blocked = map.GridCoords.Offset(new(shortRange ? 2.5f : 1.5f, 0.5f));
        EntityUid actor = default;
        EntityUid crate = default;
        NPCSteeringComponent steering = default!;

        await server.WaitAssertion(() =>
        {
            var maps = server.System<SharedMapSystem>();
            var tile = new Tile(server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var x = 1; x <= 3; x++)
                maps.SetTile(map.Grid.Owner, map.Grid.Comp, map.GridCoords.Offset(new(x, 0)), tile);
            var gravity = server.EntMan.EnsureComponent<GravityComponent>(map.Grid);
            server.System<GravitySystem>().EnableGravity(map.Grid, gravity);
            actor = server.EntMan.SpawnEntity("MobHumanPathDummy", start);
            crate = server.EntMan.SpawnEntity("CrateGenericSteel", blocked);
            var transform = server.EntMan.GetComponent<TransformComponent>(crate);
            if (anchored)
                server.System<SharedTransformSystem>().AnchorEntity(crate, transform);
            Assert.That(transform.Anchored, Is.EqualTo(anchored));
            var weapon = server.EntMan.SpawnEntity("Katana", start);
            Assert.That(server.System<SharedHandsSystem>().TryPickupAnyHand(actor, weapon), Is.True);
            server.EntMan.GetComponent<MeleeWeaponComponent>(weapon).Range = weaponRange;
        });
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            var node = server.System<PathfindingSystem>().GetPoly(blocked);
            Assert.That(node, Is.Not.Null);
            var obstacles = new List<EntityUid>();
            var system = server.System<NPCSteeringSystem>();
            Assert.That(system.FindObstacleEntities(actor, node!, obstacles), Is.Not.Null);
            Assert.That(obstacles, Does.Contain(crate));
            // Start the steering clock after the navmesh is ready, as a real movement plan would.
            steering = system.Register(actor, blocked.Offset(new(1, 0)));
            steering.Flags = clearsBlocker ? PathFlags.Smashing : PathFlags.None;
            steering.CurrentPath.Enqueue(node!);
            server.EntMan.EnsureComponent<ActiveNPCComponent>(actor);
        });
        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            var damage = server.EntMan.GetComponent<DamageableComponent>(crate).TotalDamage.Float();
            if (shortRange)
                Assert.That(damage, Is.Zero, "The actor should start outside its weapon range.");
            else
                Assert.That(damage, Is.GreaterThan(0));
            Assert.That(steering.Status, Is.EqualTo(SteeringStatus.Moving), "Approach and cooldown should keep steering active.");
        });
        await server.WaitRunTicks(450);
        await server.WaitAssertion(() =>
        {
            var exists = server.EntMan.TryGetComponent<DamageableComponent>(crate, out var damage);
            var position = server.EntMan.GetComponent<TransformComponent>(actor).Coordinates;
            Assert.That(exists, Is.EqualTo(!clearsBlocker),
                $"Crate damage: {damage?.TotalDamage}. Steering: {steering.Status}. Actor: {position}.");
            if (!clearsBlocker)
                Assert.That(steering.Status, Is.EqualTo(SteeringStatus.NoPath), "An NPC without smashing must stop at the blocker.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MovingPryTargetOffNodeCancelsItsActionBeforeRebuild()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var pathfinding = server.System<PathfindingSystem>();
        var steeringSystem = server.System<NPCSteeringSystem>();
        var blocked = map.GridCoords.Offset(new(1.5f, 0.5f));
        EntityUid door = default;
        EntityUid actor = default;
        NPCSteeringComponent steering = default!;

        await server.WaitPost(() =>
        {
            var maps = server.System<SharedMapSystem>();
            var tile = new Tile(server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            maps.SetTile(map.Grid.Owner, map.Grid.Comp, blocked, tile);
            maps.SetTile(map.Grid.Owner, map.Grid.Comp, blocked.Offset(new(0, 1)), tile);
            door = server.EntMan.SpawnEntity("Airlock", blocked);
            actor = server.EntMan.SpawnEntity("MobHumanPathDummy", map.GridCoords.Offset(new(0.5f, 0.5f)));
            server.EntMan.EnsureComponent<PryingComponent>(actor).PryPowered = true;
            steering = steeringSystem.Register(actor, map.GridCoords.Offset(new(2.5f, 0.5f)));
            steering.Flags = PathFlags.Prying;
        });
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            var node = pathfinding.GetPoly(blocked);
            Assert.That(node, Is.Not.Null);
            steering.CurrentPath.Enqueue(node!);
            server.EntMan.EnsureComponent<ActiveNPCComponent>(actor);
            steeringSystem.Update(1f / 30);

            var action = steering.DoAfterId;
            var doAfter = server.System<SharedDoAfterSystem>();
            Assert.That(steering.ObstacleActionTarget, Is.EqualTo(door));
            Assert.That(doAfter.GetStatus(action), Is.EqualTo(DoAfterStatus.Running));
            var transforms = server.System<SharedTransformSystem>();
            transforms.Unanchor(door, server.EntMan.GetComponent<TransformComponent>(door));
            transforms.SetCoordinates(door, blocked.Offset(new(0, 1)));
            Assert.That(node!.IsValid(), Is.True, "The stale node must still be in use for this check.");
            steeringSystem.Update(1f / 30);

            Assert.That(doAfter.GetStatus(action), Is.EqualTo(DoAfterStatus.Cancelled));
            Assert.That(steering.ObstacleActionTarget, Is.Null);
            Assert.That(steering.Status, Is.EqualTo(SteeringStatus.Moving));
        });

        await pair.CleanReturnAsync();
    }
}
