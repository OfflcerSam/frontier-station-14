#nullable enable
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server.Access.Systems;
using Content.Server.Atmos.Components;
using Content.Server.Body.Components;
using Content.Server.Destructible;
using Content.Server.Gravity;
using Content.Server.NPC;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Server.Storage.EntitySystems;
using Content.Server.Temperature.Components;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared.Access.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Gravity;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Lock;
using Content.Shared.NPC;
using Content.Shared.NPC.Systems;
using Content.Shared.Storage.Components;
using Content.Shared.Tools.Systems;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests._NF.NPC;

[TestFixture, Category("AILogicRegression")]
public sealed class AIStorageCombatTest
{
    [TestCase("CrateGenericSteel")]
    [TestCase("ClosetSteelBase")]
    public async Task MeleeOpensStorageWithoutReplacingTheHostileTarget(string prototype)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            var start = map.GridCoords.Offset(new(0.5f, 0.5f));
            var inside = start.Offset(new(1, 0));
            var tile = new Tile(server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            server.System<SharedMapSystem>().SetTile(map.Grid.Owner, map.Grid.Comp, inside, tile);
            var actor = entities.SpawnEntity("MobHumanPathDummy", start);
            var hostile = entities.SpawnEntity("MobHumanPathDummy", inside);
            var storage = entities.SpawnEntity(prototype, inside);
            Assert.That(server.System<EntityStorageSystem>().Insert(hostile, storage), Is.True);
            entities.EnsureComponent<ActiveNPCComponent>(actor);
            var combat = entities.EnsureComponent<NPCMeleeCombatComponent>(actor);
            combat.Target = hostile;
            var movement = server.System<NPCSteeringSystem>().Register(actor, new EntityCoordinates(hostile, Vector2.Zero));
            movement.Flags = PathFlags.Interact;

            server.System<NPCCombatSystem>().Update(1f / 30);

            Assert.That(entities.GetComponent<EntityStorageComponent>(storage).Open, Is.True);
            Assert.That(combat.Target, Is.EqualTo(hostile));
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task LockedLockerRequiresAccess(bool authorized)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        await server.WaitAssertion(() =>
        {
            var start = AddFloor(server, map);
            var (actor, hostile, locker) = SpawnEncounter(server, start, start.Offset(new(1, 0)), "LockerSecurity");
            var locks = server.System<LockSystem>();
            Assert.That(locks.IsLocked(locker), Is.True);
            if (authorized)
            {
                var access = server.EntMan.EnsureComponent<AccessComponent>(actor);
                server.System<AccessSystem>().TryAddGroups(actor, ["AllAccess"], access);
            }
            var combat = StartMelee(server, actor, hostile, PathFlags.Interact);
            var system = server.System<NPCCombatSystem>();
            system.Update(1f / 30);
            Assert.That(server.EntMan.GetComponent<EntityStorageComponent>(locker).Open, Is.EqualTo(authorized));
            Assert.That(locks.IsLocked(locker), Is.EqualTo(!authorized));
            Assert.That(combat.Target, Is.EqualTo(hostile));
            Assert.That(combat.Status, Is.EqualTo(authorized ? CombatStatus.Normal : CombatStatus.TargetUnreachable));

            if (!authorized)
            {
                Assert.That(server.EntMan.HasComponent<NPCSteeringComponent>(actor), Is.False);
                var attempt = server.EntMan.GetComponent<NPCStorageCombatComponent>(actor);
                var deadline = attempt.Deadline;
                system.Update(1f / 30);
                Assert.That(attempt.Deadline, Is.EqualTo(deadline));
                Assert.That(attempt.Failed, Is.True);
            }
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("CrateGenericSteel", true, false)]
    [TestCase("ClosetSteelBase", true, false)]
    [TestCase("CrateGenericSteel", false, false)]
    [TestCase("CrateGenericSteel", false, true)]
    public async Task StorageCapabilityDeterminesOpeningOrBreaching(string prototype, bool welded, bool canSmash)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        EntityUid actor = default;
        EntityUid hostile = default;
        EntityUid storage = default;
        await server.WaitAssertion(() =>
        {
            var start = AddFloor(server, map);
            (actor, hostile, storage) = SpawnEncounter(server, start, start.Offset(new(1, 0)), prototype);
            if (welded)
            {
                var welds = server.System<WeldableSystem>();
                welds.SetWeldedState(storage, true);
                Assert.That(welds.IsWelded(storage), Is.True);
            }
            else if (!canSmash)
            {
                server.EntMan.RemoveComponent<HandsComponent>(actor);
            }

            if (canSmash)
            {
                server.System<WeldableSystem>().SetWeldedState(storage, true);
                var katana = server.EntMan.SpawnEntity("Katana", start);
                Assert.That(server.System<SharedHandsSystem>().TryPickupAnyHand(actor, katana), Is.True);
            }
        });

        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            var flags = canSmash ? PathFlags.Interact | PathFlags.Smashing : PathFlags.Interact;
            var combat = StartMelee(server, actor, hostile, flags);
            server.System<NPCCombatSystem>().Update(1f / 30);
            Assert.That(server.EntMan.GetComponent<EntityStorageComponent>(storage).Open, Is.False);
            Assert.That(server.EntMan.GetComponent<DamageableComponent>(storage).TotalDamage.Float() > 0, Is.EqualTo(canSmash));
            Assert.That(server.EntMan.GetComponent<DamageableComponent>(hostile).TotalDamage.Float(), Is.Zero);
            Assert.That(combat.Status, Is.EqualTo(canSmash ? CombatStatus.Normal : CombatStatus.TargetUnreachable));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NestedStorageOpensFromTheOutsideIn()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        await server.WaitAssertion(() =>
        {
            var start = AddFloor(server, map);
            var inside = start.Offset(new(1, 0));
            var (actor, hostile, inner) = SpawnEncounter(server, start, inside, "CrateGenericSteel");
            var outer = server.EntMan.SpawnEntity("ClosetSteelBase", inside);
            Assert.That(server.System<EntityStorageSystem>().Insert(inner, outer), Is.True);
            var combat = StartMelee(server, actor, hostile, PathFlags.Interact);
            var system = server.System<NPCCombatSystem>();

            system.Update(1f / 30);
            Assert.That(server.EntMan.GetComponent<EntityStorageComponent>(outer).Open, Is.True);
            Assert.That(server.EntMan.GetComponent<EntityStorageComponent>(inner).Open, Is.False);
            system.Update(1f / 30);
            Assert.That(server.EntMan.GetComponent<EntityStorageComponent>(inner).Open, Is.True);
            Assert.That(combat.Target, Is.EqualTo(hostile));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExteriorApproachFollowsMovingStorageAndClearsAfterExit()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        EntityUid actor = default;
        EntityUid hostile = default;
        EntityUid storage = default;
        await server.WaitAssertion(() =>
        {
            var start = AddFloor(server, map);
            var gravity = server.EntMan.EnsureComponent<GravityComponent>(map.Grid);
            server.System<GravitySystem>().EnableGravity(map.Grid, gravity);
            (actor, hostile, storage) = SpawnEncounter(server, start, start.Offset(new(4, 0)), "CrateGenericSteel");
        });
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            var start = map.GridCoords.Offset(new(0.5f, 0.5f));
            StartMelee(server, actor, hostile, PathFlags.Interact);
            var combat = server.System<NPCCombatSystem>();
            combat.Update(1f / 30);
            var steering = server.EntMan.GetComponent<NPCSteeringComponent>(actor);
            var goal = steering.Coordinates;
            Assert.That(goal.EntityId, Is.EqualTo(storage));
            Assert.That(goal.Position, Is.Not.EqualTo(Vector2.Zero));
            var transform = server.System<SharedTransformSystem>();
            var before = transform.ToMapCoordinates(goal).Position;
            transform.SetCoordinates(storage, start.Offset(new(5, 0)));
            combat.Update(1f / 30);
            Assert.That(steering.Coordinates, Is.EqualTo(goal));
            Assert.That(transform.ToMapCoordinates(goal).Position, Is.EqualTo(before + Vector2.UnitX));
        });

        await AssertStorageOpens(server, actor, storage);
        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(server.EntMan.GetComponent<NPCMeleeCombatComponent>(actor).Target, Is.EqualTo(hostile));
            Assert.That(server.EntMan.GetComponent<NPCSteeringComponent>(actor).Coordinates.EntityId, Is.EqualTo(hostile));
            Assert.That(server.EntMan.HasComponent<NPCStorageCombatComponent>(actor), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ApproachChoosesAnotherFaceWhenTheClosestIsBlocked()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        EntityUid actor = default;
        EntityUid hostile = default;
        EntityUid storage = default;
        await server.WaitAssertion(() =>
        {
            var start = AddFloor(server, map);
            var gravity = server.EntMan.EnsureComponent<GravityComponent>(map.Grid);
            server.System<GravitySystem>().EnableGravity(map.Grid, gravity);
            (actor, hostile, storage) = SpawnEncounter(server, start, start.Offset(new(4, 0)), "CrateGenericSteel");
            server.EntMan.SpawnEntity("ReinforcedWindow", start.Offset(new(3, 0)));
        });
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            StartMelee(server, actor, hostile, PathFlags.Interact);
            server.System<NPCCombatSystem>().Update(1f / 30);
            var attempt = server.EntMan.GetComponent<NPCStorageCombatComponent>(actor);
            Assert.That(attempt.ApproachIndex, Is.GreaterThan(0));
            Assert.That(attempt.Goal?.EntityId, Is.EqualTo(storage));
        });
        await AssertStorageOpens(server, actor, storage);
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RangedActorCanFinishABreachWithMeleeWhenItsGunIsGone()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        EntityUid actor = default;
        EntityUid hostile = default;
        EntityUid storage = default;
        await server.WaitAssertion(() =>
        {
            var start = AddFloor(server, map);
            (actor, hostile, storage) = SpawnEncounter(server, start, start.Offset(new(1, 0)), "CrateGenericSteel");
            server.System<WeldableSystem>().SetWeldedState(storage, true);
            var katana = server.EntMan.SpawnEntity("Katana", start);
            Assert.That(server.System<SharedHandsSystem>().TryPickupAnyHand(actor, katana), Is.True);
            var threshold = server.System<DestructibleSystem>().DestroyedAt(storage);
            DamageStorage(server, storage, threshold.Int() - 1);
        });
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            var goal = new EntityCoordinates(hostile, Vector2.Zero);
            var steering = server.System<NPCSteeringSystem>().Register(actor, goal);
            steering.Flags = PathFlags.Smashing;
            var combat = server.EntMan.EnsureComponent<NPCRangedCombatComponent>(actor);
            var system = server.System<NPCCombatSystem>();
            system.SetRangedTarget((actor, combat), hostile, goal);
            system.Update(1f / 30);
            Assert.That(server.EntMan.GetComponent<NPCStorageCombatComponent>(actor).UseMelee, Is.True);
        });
        await server.WaitRunTicks(5);
        await server.WaitAssertion(() =>
        {
            var combat = server.EntMan.GetComponent<NPCRangedCombatComponent>(actor);
            Assert.That(server.EntMan.EntityExists(storage), Is.False);
            Assert.That(server.System<NPCCombatSystem>().FindStorageBlocker(hostile, out _), Is.False);
            Assert.That(combat.Target, Is.EqualTo(hostile));
            Assert.That(server.EntMan.GetComponent<NPCSteeringComponent>(actor).Coordinates.EntityId, Is.EqualTo(hostile));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExistingDamageDoesNotStartShotTimeoutWhileTurning()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        EntityUid actor = default;
        EntityUid hostile = default;
        EntityUid storage = default;
        EntityUid weapon = default;
        await server.WaitAssertion(() =>
        {
            var start = AddFloor(server, map);
            (actor, hostile, storage) = SpawnEncounter(server, start, start.Offset(new(3, 0)), "CrateGenericSteel");
            server.System<WeldableSystem>().SetWeldedState(storage, true);
            DamageStorage(server, storage, 5);
            weapon = server.EntMan.SpawnEntity("NFWeaponEnergyPistolLaser", start);
            Assert.That(server.System<SharedHandsSystem>().TryPickupAnyHand(actor, weapon), Is.True);
        });
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            var goal = new EntityCoordinates(hostile, Vector2.Zero);
            var steering = server.System<NPCSteeringSystem>().Register(actor, goal);
            steering.Flags = PathFlags.Smashing;
            var combat = server.EntMan.EnsureComponent<NPCRangedCombatComponent>(actor);
            server.System<NPCCombatSystem>().SetRangedTarget((actor, combat), hostile, goal);
            combat.RotationSpeed = Angle.FromDegrees(5);
            combat.AccuracyThreshold = Angle.Zero;
            server.System<SharedTransformSystem>().SetWorldRotation(actor, Angle.FromWorldVec(-Vector2.UnitX));
        });
        await server.WaitRunTicks(120);
        await server.WaitAssertion(() =>
        {
            var attempt = server.EntMan.GetComponent<NPCStorageCombatComponent>(actor);
            Assert.That(attempt.DamageDeadline, Is.Null);
            Assert.That(attempt.Failed, Is.False);
            Assert.That(server.EntMan.GetComponent<GunComponent>(weapon).ShootCoordinates, Is.Null);

            var deadline = attempt.Deadline;
            server.EntMan.RemoveComponent<NPCRangedCombatComponent>(actor);
            var goal = new EntityCoordinates(hostile, Vector2.Zero);
            server.System<NPCSteeringSystem>().Register(actor, goal).Flags = PathFlags.Smashing;
            var resumed = server.EntMan.EnsureComponent<NPCRangedCombatComponent>(actor);
            server.System<NPCCombatSystem>().SetRangedTarget((actor, resumed), hostile, goal);
            Assert.That(server.EntMan.GetComponent<NPCStorageCombatComponent>(actor), Is.SameAs(attempt));
            Assert.That(attempt.Deadline, Is.EqualTo(deadline));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task UtilityKeepsOnlyKnownOccupantsAndStopsSelectingAFailedBlocker()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var board = new NPCBlackboard();
        EntityUid actor = default;
        EntityUid hostile = default;
        EntityUid storage = default;
        TimeSpan deadline = default;
        await server.WaitAssertion(() =>
        {
            var start = AddFloor(server, map);
            (actor, hostile, storage) = SpawnEncounter(server, start, start.Offset(new(1, 0)), "CrateGenericSteel");
            var factions = server.System<NpcFactionSystem>();
            var hostileFaction = "SimpleHostile";
            factions.ClearFactions(hostile);
            factions.AddFaction(hostile, hostileFaction);
            server.System<WeldableSystem>().SetWeldedState(storage, true);
            board.SetValue(NPCBlackboard.Owner, actor);
            board.SetValue(NPCBlackboard.NavInteract, true);
            var utility = server.System<NPCUtilitySystem>();
            Assert.That(utility.GetEntities(board, "NearbyMeleeTargets").GetHighest(), Is.EqualTo(EntityUid.Invalid));
            board.SetValue("Target", hostile);
            Assert.That(utility.GetEntities(board, "NearbyMeleeTargets").GetHighest(), Is.EqualTo(hostile));

            StartMelee(server, actor, hostile, PathFlags.Interact);
            var combat = server.System<NPCCombatSystem>();
            combat.Update(1f / 30);
            deadline = server.EntMan.GetComponent<NPCStorageCombatComponent>(actor).Deadline;
            Assert.That(utility.GetEntities(board, "NearbyMeleeTargets").GetHighest(), Is.EqualTo(EntityUid.Invalid));
            board.Remove<EntityUid>("Target");
        });
        await server.WaitRunTicks(5);
        await server.WaitAssertion(() =>
        {
            server.System<WeldableSystem>().SetWeldedState(storage, false);
            Assert.That(server.System<NPCUtilitySystem>().GetEntities(board, "NearbyMeleeTargets").GetHighest(), Is.EqualTo(hostile));
            server.System<NPCSteeringSystem>().Register(actor, new EntityCoordinates(hostile, Vector2.Zero)).Flags = PathFlags.Interact;
            server.System<NPCCombatSystem>().Update(1f / 30);
            Assert.That(server.EntMan.GetComponent<NPCStorageCombatComponent>(actor).Deadline, Is.EqualTo(deadline));
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RunningCombatPlanKeepsKnownStorageThroughPlanChanges(bool initiallyWelded)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { ServerSeed = 12345 });
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        EntityUid actor = default;
        EntityUid hostile = default;
        EntityUid storage = default;
        await server.WaitAssertion(() =>
        {
            var start = AddFloor(server, map);
            var gravity = server.EntMan.EnsureComponent<GravityComponent>(map.Grid);
            server.System<GravitySystem>().EnableGravity(map.Grid, gravity);
            actor = server.EntMan.SpawnEntity("MobHumanPathDummy", start);
            hostile = SpawnDamageTarget(server, start.Offset(new(5, 0)));
            storage = server.EntMan.SpawnEntity("CrateGenericSteel", start.Offset(new(5, 1)));
            var factions = server.System<NpcFactionSystem>();
            var attackerFaction = "SimpleHostile";
            var targetFaction = "NanoTrasen";
            factions.ClearFactions(actor);
            factions.AddFaction(actor, attackerFaction);
            factions.AddFaction(hostile, targetFaction);
            var htn = server.EntMan.EnsureComponent<HTNComponent>(actor);
            htn.RootTask = new HTNCompoundTask { Task = "SimpleHostileCompound" };
            htn.Blackboard.SetValue(NPCBlackboard.Owner, actor);
            htn.Blackboard.SetValue(NPCBlackboard.NavInteract, true);
        });
        Assert.That(await WaitFor(server, () =>
            server.EntMan.TryGetComponent<NPCMeleeCombatComponent>(actor, out var melee) && melee.Target == hostile),
            Is.True, "The normal HTN plan should acquire the exposed hostile.");
        await server.WaitAssertion(() =>
        {
            Assert.That(server.System<EntityStorageSystem>().Insert(hostile, storage), Is.True);
            if (initiallyWelded)
                server.System<WeldableSystem>().SetWeldedState(storage, true);
        });
        if (initiallyWelded)
        {
            Assert.That(await WaitFor(server, () =>
                server.EntMan.TryGetComponent<NPCStorageCombatComponent>(actor, out var attempt) && attempt.Failed &&
                !server.EntMan.GetComponent<HTNComponent>(actor).Blackboard.ContainsKey("Target")), Is.True);
            await server.WaitPost(() => server.System<WeldableSystem>().SetWeldedState(storage, false));
            Assert.That(await WaitFor(server, () =>
                server.EntMan.TryGetComponent<NPCMeleeCombatComponent>(actor, out var melee) && melee.Target == hostile,
                ticksPerCheck: 1), Is.True, "The real planner should reacquire the known occupant after unwelding.");
        }
        await AssertStorageOpens(server, actor, storage);
        await server.WaitAssertion(() =>
        {
            Assert.That(server.System<NPCCombatSystem>().FindStorageBlocker(hostile, out _), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("NFWeaponEnergyPistolLaser", false)]
    [TestCase("NFWeaponEnergyPistolLaserMakeshift", true)]
    public async Task RangedBreachTargetsTheCrateAndResumesTheOriginalPursuit(string weaponPrototype, bool retarget)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var start = map.GridCoords.Offset(new(0.5f, 0.5f));
        EntityUid actor = default;
        EntityUid weapon = default;
        EntityUid storage = default;
        EntityUid hostile = default;
        await server.WaitAssertion(() =>
        {
            AddFloor(server, map);
            (actor, hostile, storage) = SpawnEncounter(server, start, start.Offset(new(3, 0)), "CrateGenericSteel");
            server.System<WeldableSystem>().SetWeldedState(storage, true);
            weapon = server.EntMan.SpawnEntity(weaponPrototype, start);
            Assert.That(server.System<SharedHandsSystem>().TryPickupAnyHand(actor, weapon), Is.True);
        });
        await server.WaitRunTicks(30);
        var goal = new EntityCoordinates(hostile, Vector2.Zero);
        await server.WaitAssertion(() =>
        {
            var combat = server.EntMan.EnsureComponent<NPCRangedCombatComponent>(actor);
            var steering = server.System<NPCSteeringSystem>().Register(actor, goal);
            steering.Flags = PathFlags.Interact | PathFlags.Smashing;
            steering.Range = 1f;
            var system = server.System<NPCCombatSystem>();
            system.SetRangedTarget((actor, combat), hostile, goal);
            system.Update(1f / 30);
            Assert.That(server.EntMan.GetComponent<GunComponent>(weapon).Target, Is.EqualTo(storage));
            Assert.That(combat.Target, Is.EqualTo(hostile));
            Assert.That(steering.Coordinates, Is.Not.EqualTo(goal));
        });
        Assert.That(await WaitFor(server, () =>
            server.EntMan.GetComponent<DamageableComponent>(storage).TotalDamage.Float() > 0), Is.True);
        await server.WaitAssertion(() =>
        {
            Assert.That(HeatDamage(server, hostile), Is.Zero, "Only the crate should take weapon damage.");
            var combat = server.EntMan.GetComponent<NPCRangedCombatComponent>(actor);
            var system = server.System<NPCCombatSystem>();
            EntityUid expectedTarget;
            if (retarget)
            {
                expectedTarget = server.EntMan.SpawnEntity("MobHumanPathDummy", start.Offset(new(5, 0)));
                system.SetRangedTarget((actor, combat), expectedTarget, goal);
            }
            else
            {
                expectedTarget = hostile;
                server.System<EntityStorageSystem>().Remove(hostile, storage);
                system.Update(1f / 30);
            }

            Assert.That(combat.Target, Is.EqualTo(expectedTarget));
            Assert.That(server.EntMan.GetComponent<NPCSteeringComponent>(actor).Coordinates.EntityId, Is.EqualTo(expectedTarget));
            Assert.That(server.EntMan.HasComponent<NPCStorageCombatComponent>(actor), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("NFWeaponEnergyPistolLaser")]
    [TestCase("NFWeaponEnergyPistolLaserMakeshift")]
    public async Task PlayerStyleTargetingStillBypassesAnUntargetedCrate(string weaponPrototype)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var start = map.GridCoords.Offset(new(0.5f, 0.5f));
        EntityUid actor = default;
        EntityUid weapon = default;
        EntityUid storage = default;
        EntityUid hostile = default;
        await server.WaitAssertion(() =>
        {
            AddFloor(server, map);
            actor = server.EntMan.SpawnEntity("MobHumanPathDummy", start);
            storage = server.EntMan.SpawnEntity("CrateGenericSteel", start.Offset(new(2, 0)));
            hostile = SpawnDamageTarget(server, start.Offset(new(4, 0)));
            weapon = server.EntMan.SpawnEntity(weaponPrototype, start);
            Assert.That(server.System<SharedHandsSystem>().TryPickupAnyHand(actor, weapon), Is.True);
        });
        await server.WaitRunTicks(30);
        await server.WaitPost(() =>
        {
            var gun = server.EntMan.GetComponent<GunComponent>(weapon);
            server.System<GunSystem>().AttemptShoot(actor, weapon, gun, new EntityCoordinates(hostile, Vector2.Zero), hostile);
        });
        Assert.That(await WaitFor(server, () => HeatDamage(server, hostile) > 0), Is.True);
        await server.WaitAssertion(() =>
            Assert.That(HeatDamage(server, storage), Is.Zero));
        await pair.CleanReturnAsync();
    }

    private static NPCMeleeCombatComponent StartMelee(RobustIntegrationTest.ServerIntegrationInstance server,
        EntityUid actor, EntityUid hostile, PathFlags flags)
    {
        server.EntMan.EnsureComponent<ActiveNPCComponent>(actor);
        var combat = server.EntMan.EnsureComponent<NPCMeleeCombatComponent>(actor);
        combat.Target = hostile;
        var movement = server.System<NPCSteeringSystem>().Register(actor, new EntityCoordinates(hostile, Vector2.Zero));
        movement.Flags = flags;
        return combat;
    }

    private static (EntityUid Actor, EntityUid Hostile, EntityUid Storage) SpawnEncounter(
        RobustIntegrationTest.ServerIntegrationInstance server, EntityCoordinates start, EntityCoordinates inside, string prototype)
    {
        var actor = server.EntMan.SpawnEntity("MobHumanPathDummy", start);
        var hostile = SpawnDamageTarget(server, inside);
        var storage = server.EntMan.SpawnEntity(prototype, inside);
        Assert.That(server.System<EntityStorageSystem>().Insert(hostile, storage), Is.True);
        return (actor, hostile, storage);
    }

    private static EntityUid SpawnDamageTarget(RobustIntegrationTest.ServerIntegrationInstance server, EntityCoordinates at)
    {
        var target = server.EntMan.SpawnEntity("MobHumanPathDummy", at);
        // Keep environmental damage out of weapon assertions on the headless test grid.
        server.EntMan.RemoveComponent<RespiratorComponent>(target);
        server.EntMan.RemoveComponent<TemperatureComponent>(target);
        server.EntMan.RemoveComponent<BarotraumaComponent>(target);
        return target;
    }

    private static EntityCoordinates AddFloor(RobustIntegrationTest.ServerIntegrationInstance server, TestMapData map)
    {
        var maps = server.System<SharedMapSystem>();
        var tile = new Tile(server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
        for (var x = 0; x < 8; x++)
        {
            for (var y = -1; y <= 1; y++)
                maps.SetTile(map.Grid.Owner, map.Grid.Comp, map.GridCoords.Offset(new(x, y)), tile);
        }
        return map.GridCoords.Offset(new(0.5f, 0.5f));
    }

    private static void DamageStorage(RobustIntegrationTest.ServerIntegrationInstance server, EntityUid storage, int amount)
    {
        var blunt = server.ResolveDependency<IPrototypeManager>().Index(new ProtoId<DamageTypePrototype>("Blunt"));
        server.System<DamageableSystem>().TryChangeDamage(storage, new DamageSpecifier(blunt, amount), ignoreResistances: true);
    }

    private static float HeatDamage(RobustIntegrationTest.ServerIntegrationInstance server, EntityUid entity)
    {
        var damage = server.EntMan.GetComponent<DamageableComponent>(entity).Damage.DamageDict;
        return damage.TryGetValue("Heat", out var heat) ? heat.Float() : 0f;
    }

    private static async Task AssertStorageOpens(RobustIntegrationTest.ServerIntegrationInstance server,
        EntityUid actor, EntityUid storage)
    {
        if (await WaitFor(server, () => server.EntMan.GetComponent<EntityStorageComponent>(storage).Open))
            return;

        await server.WaitAssertion(() =>
        {
            var attempt = server.EntMan.GetComponent<NPCStorageCombatComponent>(actor);
            var xform = server.EntMan.GetComponent<TransformComponent>(actor);
            var steering = server.EntMan.TryGetComponent<NPCSteeringComponent>(actor, out var movement)
                ? movement.Status.ToString()
                : "removed";
            Assert.Fail($"Storage is closed. Actor {xform.Coordinates}, approach {attempt.ApproachIndex}, goal {attempt.Goal}, " +
                $"steering {steering}, failed {attempt.Failed}.");
        });
    }

    private static async Task<bool> WaitFor(RobustIntegrationTest.ServerIntegrationInstance server, Func<bool> predicate,
        int ticksPerCheck = 5)
    {
        var result = false;
        for (var i = 0; i < 225 && !result; i += ticksPerCheck)
        {
            await server.WaitRunTicks(ticksPerCheck);
            await server.WaitPost(() => result = predicate());
        }
        return result;
    }
}
