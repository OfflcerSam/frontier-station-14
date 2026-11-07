using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Administration.Managers;
using Content.Server.DoAfter;
using Content.Server.Gravity; // Wizden#38846
using Content.Server.NPC.Components;
using Content.Server.NPC.Events;
using Content.Server.NPC.Pathfinding;
using Content.Shared.CCVar;
using Content.Shared.Climbing.Systems;
using Content.Shared.CombatMode;
using Content.Shared.DoAfter; // Frontier
using Content.Shared.Interaction;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.NPC;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.NPC.Events;
using Content.Shared.Physics;
using Content.Shared.Weapons.Melee;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Content.Shared.Prying.Systems;
using Microsoft.Extensions.ObjectPool;
using Prometheus;

namespace Content.Server.NPC.Systems;

public sealed partial class NPCSteeringSystem : SharedNPCSteeringSystem
{
    private static readonly Gauge ActiveSteeringGauge = Metrics.CreateGauge(
        "npc_steering_active_count",
        "Amount of NPCs trying to actively do steering");

    /*
     * We use context steering to determine which way to move.
     * This involves creating an array of possible directions and assigning a value for the desireability of each direction.
     *
     * There's multiple ways to implement this, e.g. you can average all directions, or you can choose the highest direction
     * , or you can remove the danger map entirely and only having an interest map (AKA game endeavour).
     * See http://www.gameaipro.com/GameAIPro2/GameAIPro2_Chapter18_Context_Steering_Behavior-Driven_Steering_at_the_Macro_Scale.pdf
     * (though in their case it was for an F1 game so used context steering across the width of the road).
     */

    [Dependency] private readonly IAdminManager _admin = default!;
    [Dependency] private readonly IConfigurationManager _configManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ClimbSystem _climb = default!;
    [Dependency] private readonly DoAfterSystem _doAfter = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly GravitySystem _gravity = default!; // Wizden#38846
    [Dependency] private readonly NpcFactionSystem _npcFaction = default!;
    [Dependency] private readonly PathfindingSystem _pathfindingSystem = default!;
    [Dependency] private readonly PryingSystem _pryingSystem = default!;
    // Frontier: obstacle lookup uses the physics broadphase instead of the anchored entity list.
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedMeleeWeaponSystem _melee = default!;
    [Dependency] private readonly SharedMoverController _mover = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedCombatModeSystem _combat = default!;

    private EntityQuery<FixturesComponent> _fixturesQuery;
    private EntityQuery<MovementSpeedModifierComponent> _modifierQuery;
    private EntityQuery<NpcFactionMemberComponent> _factionQuery;
    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<TransformComponent> _xformQuery;

    private ObjectPool<HashSet<EntityUid>> _entSetPool =
        new DefaultObjectPool<HashSet<EntityUid>>(new SetPolicy<EntityUid>());

    /// <summary>
    /// Enabled antistuck detection so if an NPC is in the same spot for a while it will re-path.
    /// </summary>
    public bool AntiStuck = true;

    private bool _enabled;

    private bool _pathfinding = true;

    public static readonly Vector2[] Directions = new Vector2[InterestDirections];

    private readonly HashSet<ICommonSession> _subscribedSessions = new();

    private object _obstacles = new();

    private int _activeSteeringCount;

    public override void Initialize()
    {
        base.Initialize();

        Log.Level = LogLevel.Info;
        _fixturesQuery = GetEntityQuery<FixturesComponent>();
        _modifierQuery = GetEntityQuery<MovementSpeedModifierComponent>();
        _factionQuery = GetEntityQuery<NpcFactionMemberComponent>();
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();

        for (var i = 0; i < InterestDirections; i++)
        {
            Directions[i] = new Angle(InterestRadians * i).ToVec();
        }

        UpdatesBefore.Add(typeof(SharedPhysicsSystem));
        Subs.CVar(_configManager, CCVars.NPCEnabled, SetNPCEnabled, true);
        Subs.CVar(_configManager, CCVars.NPCPathfinding, SetNPCPathfinding, true);

        SubscribeLocalEvent<NPCSteeringComponent, ComponentShutdown>(OnSteeringShutdown);
        SubscribeNetworkEvent<RequestNPCSteeringDebugEvent>(OnDebugRequest);
    }

    private void SetNPCEnabled(bool obj)
    {
        if (!obj)
        {
            foreach (var (comp, mover) in EntityQuery<NPCSteeringComponent, InputMoverComponent>())
            {
                mover.CurTickSprintMovement = Vector2.Zero;
                // Frontier: retire pending navigation when disabled.
                CancelPathRequest(comp);
                CancelObstacleAction(comp);
                // End Frontier
            }
        }

        _enabled = obj;
    }

    private void SetNPCPathfinding(bool value)
    {
        _pathfinding = value;

        if (!_pathfinding)
        {
            foreach (var comp in EntityQuery<NPCSteeringComponent>(true))
            {
                // Frontier: retire pending navigation when disabled.
                CancelPathRequest(comp);
                CancelObstacleAction(comp);
                // End Frontier
            }
        }
    }

    private void OnDebugRequest(RequestNPCSteeringDebugEvent msg, EntitySessionEventArgs args)
    {
        if (!_admin.IsAdmin(args.SenderSession))
            return;

        if (msg.Enabled)
            _subscribedSessions.Add(args.SenderSession);
        else
            _subscribedSessions.Remove(args.SenderSession);
    }

    // Frontier: detach navigation work before replacing or removing steering.
    private void OnSteeringShutdown(EntityUid uid, NPCSteeringComponent component, ComponentShutdown args)
    {
        CancelPathRequest(component);
        CancelObstacleAction(component);
    }

    private static void CancelPathRequest(NPCSteeringComponent component)
    {
        // Relinquish ownership immediately. The pathfinding update may retire the request later.
        var request = component.PathfindToken;
        component.PathfindToken = null;
        request?.Cancel();
    }

    private void CancelObstacleAction(NPCSteeringComponent component)
    {
        // Cancellation raises events synchronously. Detach the action before notifying listeners.
        var action = component.DoAfterId;
        component.DoAfterId = null;
        component.ObstacleActionTarget = null;
        if (_doAfter.GetStatus(action) == DoAfterStatus.Running)
            _doAfter.Cancel(action);
    }

    internal void SetPlannedPath(EntityUid uid, NPCSteeringComponent steering, List<PathPoly>? path = null)
    {
        // A new movement plan takes ownership even if it uses the same destination.
        CancelPathRequest(steering);
        CancelObstacleAction(steering);
        steering.InterruptedDoor = null;
        steering.Investigation = null;
        steering.CurrentPath.Clear();
        if (path != null)
        {
            foreach (var node in path)
                steering.CurrentPath.Enqueue(node);
        }

        steering.FailedPathCount = 0;
        steering.Status = SteeringStatus.Moving;
        // Same-goal registration preserves the old timer. Give the replacement plan time to move.
        ResetStuck(steering, Transform(uid).Coordinates);
    }

    // End Frontier

    /// <summary>
    /// Adds the AI to the steering system to move towards a specific target
    /// </summary>
    public NPCSteeringComponent Register(EntityUid uid, EntityCoordinates coordinates, NPCSteeringComponent? component = null)
    {
        if (Resolve(uid, ref component, false))
        {
            // Frontier: preserve same-goal failures and reset state only when the goal changes.
            // Callers can register the same goal every tick. Only a new goal or an explicit
            // replacement plan should restart a failed attempt.
            if (component.Coordinates.Equals(coordinates))
                return component;

            CancelPathRequest(component);
            CancelObstacleAction(component);
            component.InterruptedDoor = null;
            component.Investigation = null;
            component.CurrentPath.Clear();
            component.FailedPathCount = 0;
            component.Status = SteeringStatus.Moving;
            component.LineOfSightTimer = 0;
            component.ForceMove = false;
            // End Frontier
        }
        else
        {
            component = AddComp<NPCSteeringComponent>(uid);
            component.Flags = _pathfindingSystem.GetFlags(uid);
        }

        ResetStuck(component, Transform(uid).Coordinates);
        component.Coordinates = coordinates;
        return component;
    }

    /// <summary>
    /// Attempts to register the entity. Does nothing if the coordinates already registered.
    /// </summary>
    public bool TryRegister(EntityUid uid, EntityCoordinates coordinates, NPCSteeringComponent? component = null)
    {
        if (Resolve(uid, ref component, false) && component.Coordinates.Equals(coordinates))
        {
            return false;
        }

        Register(uid, coordinates, component);
        return true;
    }

    /// <summary>
    /// Stops the steering behavior for the AI and cleans up.
    /// </summary>
    public void Unregister(EntityUid uid, NPCSteeringComponent? component = null)
    {
        if (!Resolve(uid, ref component, false))
            return;

        if (TryComp(uid, out InputMoverComponent? controller))
        {
            controller.CurTickSprintMovement = Vector2.Zero;

            var ev = new SpriteMoveEvent(false);
            RaiseLocalEvent(uid, ref ev);
        }

        RemComp<NPCSteeringComponent>(uid); // Frontier: shutdown handles cancellation.
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        PruneDoorPries(); // Frontier: retire finished and cancelled ownership once per tick.
        if (!_enabled)
            return;

        // Not every mob has the modifier component so do it as a separate query.
        var npcs = new (EntityUid, NPCSteeringComponent, InputMoverComponent, TransformComponent)[Count<ActiveNPCComponent>()];

        var query = EntityQueryEnumerator<ActiveNPCComponent, NPCSteeringComponent, InputMoverComponent, TransformComponent>();
        var index = 0;

        while (query.MoveNext(out var uid, out _, out var steering, out var mover, out var xform))
        {
            npcs[index] = (uid, steering, mover, xform);
            index++;
        }

        // Dependency issues across threads.
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = 1,
        };
        var curTime = _timing.CurTime;

        _activeSteeringCount = 0;

        Parallel.For(0, index, options, i =>
        {
            var (uid, steering, mover, xform) = npcs[i];
            Steer(uid, steering, mover, xform, frameTime, curTime);
        });

        ActiveSteeringGauge.Set(_activeSteeringCount);

        if (_subscribedSessions.Count > 0)
        {
            var data = new List<NPCSteeringDebugData>(index);

            for (var i = 0; i < index; i++)
            {
                var (uid, steering, mover, _) = npcs[i];

                data.Add(new NPCSteeringDebugData(
                    GetNetEntity(uid),
                    mover.CurTickSprintMovement,
                    steering.Interest,
                    steering.Danger,
                    steering.DangerPoints));
            }

            var filter = Filter.Empty();
            filter.AddPlayers(_subscribedSessions);

            RaiseNetworkEvent(new NPCSteeringDebugEvent(data), filter);
        }
    }

    private void SetDirection(EntityUid uid, InputMoverComponent component, NPCSteeringComponent steering, Vector2 value, bool clear = true)
    {
        if (clear && value.Equals(Vector2.Zero))
        {
            steering.CurrentPath.Clear();
            Array.Clear(steering.Interest);
            Array.Clear(steering.Danger);
        }

        component.CurTickSprintMovement = value;
        component.LastInputTick = _timing.CurTick;
        component.LastInputSubTick = ushort.MaxValue;

        var ev = new SpriteMoveEvent(true);
        RaiseLocalEvent(uid, ref ev);
    }

    /// <summary>
    /// Go through each steerer and combine their vectors
    /// </summary>
    private void Steer(
        EntityUid uid,
        NPCSteeringComponent steering,
        InputMoverComponent mover,
        TransformComponent xform,
        float frameTime,
        TimeSpan curTime)
    {
        // Frontier: retire pending work when steering stops.
        if (TerminatingOrDeleted(steering.Coordinates.EntityId) || !mover.CanMove ||
            steering.Investigation is { } investigation &&
            (curTime >= investigation.ExpiresAt || TerminatingOrDeleted(investigation.Source)))
            steering.Status = SteeringStatus.NoPath;

        if (steering.Status == SteeringStatus.NoPath)
        {
            // Frontier: retire abandoned work before stopping movement.
            CancelPathRequest(steering);
            CancelObstacleAction(steering);
            // End Frontier
            SetDirection(uid, mover, steering, Vector2.Zero);
            return;
        }

        // End Frontier

        Interlocked.Increment(ref _activeSteeringCount);

        var agentRadius = steering.Radius;
        var worldPos = _transform.GetWorldPosition(xform);
        var (layer, mask) = _physics.GetHardCollision(uid);
        // Use rotation relative to parent to rotate our context vectors by.
        var offsetRot = -_mover.GetParentGridAngle(mover);

        _modifierQuery.TryGetComponent(uid, out var modifier);
        var body = _physicsQuery.GetComponent(uid);
        // Wizden#38846
        var weightless = _gravity.IsWeightless(uid);
        var moveSpeed = GetSprintSpeed(uid, modifier);
        var acceleration = GetAcceleration((uid, modifier), weightless);
        var friction = GetFriction((uid, modifier), weightless);
        // End Wizden#38846
        var dangerPoints = steering.DangerPoints;
        dangerPoints.Clear();
        Span<float> interest = stackalloc float[InterestDirections];
        Span<float> danger = stackalloc float[InterestDirections];

        // TODO: This should be fly
        steering.CanSeek = true;

        var ev = new NPCSteeringEvent(steering, xform, worldPos, offsetRot);
        RaiseLocalEvent(uid, ref ev);
        // If seek has arrived at the target node for example then immediately re-steer.
        var forceSteer = true;
        var moveMultiplier = 1f; // multiplier to acceleration we should actually move with // Wizden#38846

        EntityUid? smashTarget = null; // Frontier
        if (steering.CanSeek && !TrySeek(uid, mover, steering, body, xform, offsetRot, moveSpeed, acceleration, friction, interest, frameTime, ref forceSteer, ref moveMultiplier, out smashTarget)) // Wizden#38846, Frontier
        {
            // Frontier: retire abandoned work before stopping movement.
            CancelPathRequest(steering);
            CancelObstacleAction(steering);
            // End Frontier
            SetDirection(uid, mover, steering, Vector2.Zero);
            return;
        }

        DebugTools.Assert(!float.IsNaN(interest[0]));

        // Don't steer too frequently to avoid twitchiness.
        // This should also implicitly solve tie situations.
        // I think doing this after all the ops above is best?
        // Originally I had it way above but sometimes mobs would overshoot their tile targets.

        if (!forceSteer)
        {
            SetDirection(uid, mover, steering, steering.LastSteerDirection, false);
            return;
        }

        // Frontier: a stationary action or a coasting NPC needs no avoidance vectors.
        if (moveMultiplier == 0f)
        {
            steering.LastSteerDirection = Vector2.Zero;
            SetDirection(uid, mover, steering, Vector2.Zero, false);
            return;
        }
        // End Frontier

        // Avoid static objects like walls
        CollisionAvoidance(uid, offsetRot, worldPos, agentRadius, layer, mask, xform, danger, smashTarget); // Frontier
        DebugTools.Assert(!float.IsNaN(danger[0]));

        Separation(uid, offsetRot, worldPos, agentRadius, layer, mask, body, xform, danger);

        // Blend last and current tick
        Blend(steering, frameTime, interest, danger);

        // Remove the danger map from the interest map.
        var desiredDirection = -1;
        var desiredValue = 0f;

        for (var i = 0; i < InterestDirections; i++)
        {
            var adjustedValue = Math.Clamp(steering.Interest[i] - steering.Danger[i], 0f, 1f);

            if (adjustedValue > desiredValue)
            {
                desiredDirection = i;
                desiredValue = adjustedValue;
            }
        }

        var resultDirection = Vector2.Zero;

        if (desiredDirection != -1)
        {
            resultDirection = new Angle(desiredDirection * InterestRadians).ToVec() * moveMultiplier; // Wizden#38846
        }

        steering.LastSteerDirection = resultDirection;
        DebugTools.Assert(!float.IsNaN(resultDirection.X));
        SetDirection(uid, mover, steering, resultDirection, false);
    }

    private EntityCoordinates GetCoordinates(PathPoly poly)
    {
        if (!poly.IsValid())
            return EntityCoordinates.Invalid;

        return new EntityCoordinates(poly.GraphUid, poly.Box.Center);
    }

    /// <summary>
    /// Get a new job from the pathfindingsystem
    /// </summary>
    // Frontier: tie path completions to the current goal and steering instance.
    private async void RequestPath(EntityUid uid, NPCSteeringComponent steering, TransformComponent xform, float targetDistance, bool invalidatedPath)
    {
        // Keep one pending search per goal. Normally we steer directly over short distances,
        // but an invalidated route needs a replacement search even when the destination is close.
        if (steering.Pathfind || (!invalidatedPath && targetDistance < steering.RepathRange))
            return;

        // Short-circuit with no path.
        var targetPoly = _pathfindingSystem.GetPoly(steering.Coordinates);

        // If this still causes issues future sloth adjust the collision mask.
        // Thanks past sloth I already realised.
        if (targetPoly != null &&
            steering.Coordinates.Position.Equals(Vector2.Zero) &&
            TryComp<PhysicsComponent>(uid, out var physics) &&
            _interaction.InRangeUnobstructed(uid, steering.Coordinates.EntityId, range: 30f, (CollisionGroup)physics.CollisionMask))
        {
            steering.CurrentPath.Clear();
            steering.CurrentPath.Enqueue(targetPoly);
            steering.FailedPathCount = 0;
            return;
        }

        using var request = new CancellationTokenSource();
        steering.PathfindToken = request;
        var destination = steering.Coordinates;

        var result = await _pathfindingSystem.GetPathSafe(
            uid,
            xform.Coordinates,
            destination,
            steering.Range,
            request.Token,
            _pathfindingSystem.GetFlags(uid));

        CompletePathRequest(uid, steering, request, destination, result);
    }

    internal void CompletePathRequest(
        EntityUid uid,
        NPCSteeringComponent steering,
        CancellationTokenSource request,
        EntityCoordinates destination,
        PathResultEvent result)
    {
        // Check ownership before clearing the marker or updating failure state. An older result
        // may arrive after a new search has claimed the slot.
        if (steering.PathfindToken != request)
            return;

        steering.PathfindToken = null;

        if (request.IsCancellationRequested ||
            TerminatingOrDeleted(uid) ||
            !TryComp(uid, out NPCSteeringComponent? current) || current != steering ||
            !steering.Coordinates.Equals(destination) ||
            TerminatingOrDeleted(destination.EntityId) ||
            steering.Status == SteeringStatus.NoPath)
        {
            return;
        }

        if (result.Result == PathResult.NoPath)
        {
            steering.CurrentPath.Clear();
            steering.FailedPathCount++;

            if (steering.FailedPathCount >= NPCSteeringComponent.FailedPathLimit)
            {
                steering.Status = SteeringStatus.NoPath;
            }

            return;
        }

        // A navmesh update can invalidate polygons between the search and its continuation.
        foreach (var poly in result.Path)
        {
            if (!poly.IsValid() || TerminatingOrDeleted(poly.GraphUid))
                return;
        }

        var targetPos = _transform.ToMapCoordinates(destination);
        var ourPos = _transform.GetMapCoordinates(uid);
        PrunePath(uid, ourPos, targetPos.Position - ourPos.Position, result.Path);
        steering.CurrentPath = new Queue<PathPoly>(result.Path);
        steering.FailedPathCount = 0;
    }

    // End Frontier

    // TODO: Move these to movercontroller

    private float GetSprintSpeed(EntityUid uid, MovementSpeedModifierComponent? modifier = null)
    {
        if (!Resolve(uid, ref modifier, false))
        {
            return MovementSpeedModifierComponent.DefaultBaseSprintSpeed;
        }

        return modifier.CurrentSprintSpeed;
    }
    // Wizden#38846
    private float GetAcceleration(Entity<MovementSpeedModifierComponent?> ent, bool weightless)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return weightless ? MovementSpeedModifierComponent.DefaultWeightlessAcceleration : MovementSpeedModifierComponent.DefaultAcceleration;

        return weightless ? ent.Comp.WeightlessAcceleration : ent.Comp.Acceleration;
    }

    private float GetFriction(Entity<MovementSpeedModifierComponent?> ent, bool weightless)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return weightless ? MovementSpeedModifierComponent.DefaultWeightlessFriction : MovementSpeedModifierComponent.DefaultFriction;

        return weightless ? ent.Comp.WeightlessFriction : ent.Comp.Friction;
    }
    // End Wizden#38846
}
