#nullable enable
using System.Linq;
using System.Numerics;
using System.Threading;
using Content.Server.NPC;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Content.Server.NPC.HTN.PrimitiveTasks.Operators;
using Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Wieldable;
using Content.Shared.Wieldable.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.UnitTesting;

namespace Content.IntegrationTests._NF.NPC;

[TestFixture, Category("AILogicRegression")]
public sealed class AIRangedTargetingTest
{
    [Test]
    public async Task ServiceTargetSwitchRetiresTheOldPursuit()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        using var oldRequest = new CancellationTokenSource();
        using var currentRequest = new CancellationTokenSource();

        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            var actor = entities.SpawnEntity("MobHumanPathDummy", map.GridCoords);
            var first = entities.SpawnEntity("MobHumanPathDummy", map.GridCoords.Offset(new(6, 0)));
            var second = entities.SpawnEntity("MobHumanPathDummy", map.GridCoords.Offset(new(0, 6)));
            var firstGoal = new EntityCoordinates(first, Vector2.Zero);
            var secondGoal = new EntityCoordinates(second, Vector2.Zero);
            var blackboard = new NPCBlackboard();
            blackboard.SetValue(NPCBlackboard.Owner, actor);
            blackboard.SetValue("Target", first);
            blackboard.SetValue("TargetCoordinates", firstGoal);
            var tasks = GetOperators(server, "GunCombatCompound");
            tasks.OfType<MoveToOperator>().Single().Startup(blackboard);
            var gun = tasks.OfType<GunOperator>().Single();
            gun.Startup(blackboard);

            var steering = entities.GetComponent<NPCSteeringComponent>(actor);
            var combat = entities.GetComponent<NPCRangedCombatComponent>(actor);
            steering.PathfindToken = oldRequest;
            steering.FailedPathCount = NPCSteeringComponent.FailedPathLimit;
            steering.Status = SteeringStatus.NoPath;
            steering.ForceMove = true;
            combat.Status = CombatStatus.TargetUnreachable;
            combat.TargetInLOS = true;
            combat.LOSAccumulator = NPCCombatSystem.UnoccludedCooldown;
            combat.ShootAccumulator = 1f;

            // The real utility service writes this key before running the current operator.
            blackboard.SetValue("Target", second);
            var status = gun.Update(blackboard, 1f / 60);
            Assert.That(combat.Target, Is.EqualTo(second));
            Assert.That(steering.Coordinates, Is.EqualTo(secondGoal));
            Assert.That(blackboard.GetValue<EntityCoordinates>("TargetCoordinates"), Is.EqualTo(secondGoal));
            Assert.That(oldRequest.IsCancellationRequested, Is.True);
            Assert.That(steering.ForceMove, Is.False);
            Assert.That(combat.TargetInLOS, Is.False);
            Assert.That(combat.ShootAccumulator, Is.Zero);
            Assert.That(status, Is.EqualTo(HTNOperatorStatus.Continuing));

            server.System<NPCSteeringSystem>().CompletePathRequest(actor, steering, oldRequest, firstGoal,
                new PathResultEvent(PathResult.NoPath, []));
            Assert.That(steering.Status, Is.EqualTo(SteeringStatus.Moving));
            Assert.That(steering.FailedPathCount, Is.Zero);

            var transform = server.System<SharedTransformSystem>();
            transform.SetCoordinates(second, map.GridCoords.Offset(new(2, 4)));
            steering.PathfindToken = currentRequest;
            combat.ShootAccumulator = 0.1f;
            gun.Update(blackboard, 1f / 60);
            Assert.That(transform.ToMapCoordinates(steering.Coordinates).Position,
                Is.EqualTo(transform.GetWorldPosition(second)));
            Assert.That(steering.PathfindToken, Is.SameAs(currentRequest));
            Assert.That(combat.ShootAccumulator, Is.EqualTo(0.1f));

            // An empty utility result retires the last target and its route.
            blackboard.SetValue("Target", EntityUid.Invalid);
            Assert.That(gun.Update(blackboard, 1f / 60), Is.EqualTo(HTNOperatorStatus.Failed));
            Assert.That(currentRequest.IsCancellationRequested, Is.True);
            Assert.That(entities.HasComponent<NPCSteeringComponent>(actor), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(-1)]
    [TestCase(0)]
    [TestCase(1)]
    public async Task AimErrorDoesNotChangeVisibilityAtSixMetres(int offsetDirection)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { ServerSeed = 12345 });
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var start = map.GridCoords.Offset(new(0.5f, 0.5f));
        var (actor, weapon) = await SpawnArmedActor(server, start);

        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            var target = entities.SpawnEntity("MobHumanPathDummy", start.Offset(new(6, 0)));
            var combat = entities.EnsureComponent<NPCRangedCombatComponent>(actor);
            combat.Target = target;
            combat.MissChance = 1f;
            combat.ShootDelay = 0f;

            // Exercise toward, perpendicular, and away offsets with the real game RNG.
            IRobustRandom sample = new RobustRandom();
            var seed = Enumerable.Range(0, 100).First(seed =>
            {
                sample.SetSeed(seed);
                sample.Prob(1f);
                var offset = sample.NextVector2(1f, 2f);
                return offsetDirection == 0 ? MathF.Abs(offset.X) < 0.1f : offset.X * offsetDirection > 1.4f;
            });
            sample.SetSeed(seed);
            sample.Prob(1f);
            var expectedOffset = sample.NextVector2(1f, 2f);
            server.ResolveDependency<IRobustRandom>().SetSeed(seed);
            server.System<NPCCombatSystem>().Update(1f / 60);

            Assert.That(combat.TargetInLOS, Is.True);
            Assert.That(combat.Status, Is.EqualTo(CombatStatus.Normal));
            var gun = entities.GetComponent<GunComponent>(weapon);
            Assert.That(gun.ShootCoordinates, Is.Not.Null);
            var transform = server.System<SharedTransformSystem>();
            var shot = transform.ToMapCoordinates(gun.ShootCoordinates!.Value).Position;
            Assert.That(Vector2.Distance(shot, transform.GetWorldPosition(target) + expectedOffset), Is.LessThan(0.001f));
            Assert.That(combat.PreparedShot, Is.Null);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CoolingGunDoesNotSampleAnotherShot()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var start = map.GridCoords.Offset(new(0.5f, 0.5f));
        var (actor, weapon) = await SpawnArmedActor(server, start);

        await server.WaitAssertion(() =>
        {
            var target = server.EntMan.SpawnEntity("MobHumanPathDummy", start.Offset(new(6, 0)));
            var combat = server.EntMan.EnsureComponent<NPCRangedCombatComponent>(actor);
            combat.Target = target;
            combat.ShootDelay = 0f;
            combat.MissChance = 0f;
            var system = server.System<NPCCombatSystem>();
            var gun = server.EntMan.GetComponent<GunComponent>(weapon);
            system.Update(1f / 60);
            var firstShot = gun.ShootCoordinates;
            Assert.That(firstShot, Is.Not.Null);

            const int seed = 424242;
            IRobustRandom expected = new RobustRandom();
            expected.SetSeed(seed);
            var random = server.ResolveDependency<IRobustRandom>();
            random.SetSeed(seed);
            combat.MissChance = 1f;
            system.Update(1f / 60);
            system.Update(1f / 60);

            Assert.That(gun.ShootCoordinates, Is.EqualTo(firstShot));
            Assert.That(combat.PreparedShot, Is.Null);
            Assert.That(random.Next(), Is.EqualTo(expected.Next()), "Cooldown should consume no aim RNG.");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StationaryTurretKeepsOneSampleWhileTurningAndClearsItOnRetarget()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            var start = map.GridCoords.Offset(new(0.5f, 0.5f));
            var actor = entities.SpawnEntity("WeaponTurretSyndicate", start);
            var first = entities.SpawnEntity("MobHumanPathDummy", start.Offset(new(6, 0)));
            var second = entities.SpawnEntity("MobHumanPathDummy", start.Offset(new(0, 6)));
            var blocked = start.Offset(new(0, 3));
            var tile = new Tile(server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            server.System<SharedMapSystem>().SetTile(map.Grid.Owner, map.Grid.Comp, blocked, tile);
            entities.SpawnEntity("WallSolid", blocked);

            var blackboard = new NPCBlackboard();
            blackboard.SetValue(NPCBlackboard.Owner, actor);
            blackboard.SetValue("Target", first);
            var unrelated = start.Offset(new(-3, 0));
            var steering = server.System<NPCSteeringSystem>().Register(actor, unrelated);
            steering.Status = SteeringStatus.NoPath;
            var gunTask = GetOperators(server, "TurretCompound").OfType<GunOperator>().Single();
            gunTask.Startup(blackboard);
            var combat = entities.GetComponent<NPCRangedCombatComponent>(actor);
            combat.RotationSpeed = Angle.FromDegrees(1);
            combat.AccuracyThreshold = Angle.FromDegrees(0);
            combat.MissChance = 1f;
            combat.ShootDelay = 0f;
            server.System<SharedTransformSystem>().SetWorldRotation(actor, Angle.FromWorldVec(-Vector2.UnitX));

            var random = server.ResolveDependency<IRobustRandom>();
            const int seed = 424242;
            random.SetSeed(seed);
            IRobustRandom expected = new RobustRandom();
            expected.SetSeed(seed);
            expected.Prob(1f);
            var offset = expected.NextVector2(1f, 2f);

            var system = server.System<NPCCombatSystem>();
            system.Update(1f / 60);
            Assert.That(combat.TargetInLOS, Is.True);
            Assert.That(combat.PreparedShot?.Offset, Is.EqualTo(offset));
            system.Update(1f / 60);
            Assert.That(combat.PreparedShot?.Offset, Is.EqualTo(offset));
            Assert.That(random.Next(), Is.EqualTo(expected.Next()), "Turning should keep the original sample.");

            blackboard.SetValue("Target", second);
            Assert.That(gunTask.Update(blackboard, 1f / 60), Is.EqualTo(HTNOperatorStatus.Continuing));
            Assert.That(combat.PreparedShot, Is.Null);
            Assert.That(combat.TargetInLOS, Is.False);
            system.Update(1f / 60);
            Assert.That(combat.Status, Is.EqualTo(CombatStatus.NotInSight));
            Assert.That(entities.GetComponent<GunComponent>(actor).ShootCoordinates, Is.Null);
            Assert.That(steering.Coordinates, Is.EqualTo(unrelated));
            Assert.That(steering.ForceMove, Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FirstProjectileAfterRetargetCarriesTheNewTarget()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var blackboard = new NPCBlackboard();
        EntityUid first = default;
        EntityUid second = default;
        NPCRangedCombatComponent combat = default!;
        GunOperator gunTask = default!;

        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            var start = map.GridCoords.Offset(new(0.5f, 0.5f));
            var actor = entities.SpawnEntity("WeaponTurretSyndicate", start);
            // The test drives the real operator explicitly so its utility service cannot pick another target.
            entities.RemoveComponent<HTNComponent>(actor);
            first = entities.SpawnEntity("MobHumanPathDummy", start.Offset(new(6, 0)));
            second = entities.SpawnEntity("MobHumanPathDummy", start.Offset(new(0, 6)));
            blackboard.SetValue(NPCBlackboard.Owner, actor);
            blackboard.SetValue("Target", first);
            gunTask = GetOperators(server, "TurretCompound").OfType<GunOperator>().Single();
            gunTask.Startup(blackboard);
            combat = entities.GetComponent<NPCRangedCombatComponent>(actor);
            combat.ShootDelay = 0f;
            combat.MissChance = 0f;
            server.System<NPCCombatSystem>().Update(1f / 60);
            Assert.That(entities.GetComponent<GunComponent>(actor).Target, Is.EqualTo(first));

            blackboard.SetValue("Target", second);
            Assert.That(gunTask.Update(blackboard, 1f / 60), Is.EqualTo(HTNOperatorStatus.Continuing));
            combat.ShootDelay = 60f;
        });

        // One half-second at the default test tick rate exceeds this turret's normal cooldown.
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            combat.ShootDelay = 0f;
            server.System<NPCCombatSystem>().Update(1f / 60);
            var projectiles = server.EntMan.EntityQuery<TargetedProjectileComponent>().Select(projectile => projectile.Target);
            Assert.That(projectiles, Does.Contain(second));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task HeldBurstContinuationRetainsTheSelectedEntity()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var start = map.GridCoords.Offset(new(0.5f, 0.5f));
        var (actor, weapon) = await SpawnArmedActor(server, start, "NFWeaponEnergyRifleAssaultLaserRepeater");
        var destination = start.Offset(new(30, 0));
        EntityUid target = default;

        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            target = entities.SpawnEntity("MobHumanPathDummy", destination);
            var wieldable = entities.GetComponent<WieldableComponent>(weapon);
            Assert.That(server.System<SharedWieldableSystem>().TryWield(weapon, wieldable, actor), Is.True);
            var gun = entities.GetComponent<GunComponent>(weapon);
            server.System<GunSystem>().AttemptShoot(actor, weapon, gun, destination, target);
            Assert.That(gun.BurstActivated, Is.True);
            Assert.That(entities.EntityQuery<TargetedProjectileComponent>().Count(projectile => projectile.Target == target),
                Is.EqualTo(1));
        });
        // The repeater fires its second round after 200 ms. The distant target keeps both projectiles in flight.
        await server.WaitRunTicks(18);
        await server.WaitAssertion(() =>
        {
            var projectiles = server.EntMan.EntityQuery<TargetedProjectileComponent>();
            Assert.That(projectiles.Count(projectile => projectile.Target == target), Is.EqualTo(2));
        });
        await pair.CleanReturnAsync();
    }

    private static HTNOperator[] GetOperators(RobustIntegrationTest.ServerIntegrationInstance server, string id)
    {
        return server.ResolveDependency<IPrototypeManager>().Index(new ProtoId<HTNCompoundPrototype>(id))
            .Branches[0].Tasks.OfType<HTNPrimitiveTask>().Select(task => task.Operator).ToArray();
    }

    private static async Task<(EntityUid Actor, EntityUid Gun)> SpawnArmedActor(
        RobustIntegrationTest.ServerIntegrationInstance server, EntityCoordinates start,
        string gunPrototype = "NFWeaponEnergyPistolLaser")
    {
        EntityUid actor = default;
        EntityUid gun = default;
        await server.WaitAssertion(() =>
        {
            actor = server.EntMan.SpawnEntity("MobHumanPathDummy", start);
            gun = server.EntMan.SpawnEntity(gunPrototype, start);
            Assert.That(server.System<SharedHandsSystem>().TryPickupAnyHand(actor, gun), Is.True);
        });
        // Selecting the gun starts a cooldown. Let it expire before adding ranged combat.
        await server.WaitRunTicks(60);
        return (actor, gun);
    }
}
