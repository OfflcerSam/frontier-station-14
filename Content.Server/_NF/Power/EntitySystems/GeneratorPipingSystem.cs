// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Content.Server._NF.Power.Components;
using Content.Server.Administration.Logs;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.NodeContainer.EntitySystems;
using Content.Server.NodeContainer.Nodes;
using Content.Server.Power.Generator;
using Content.Shared.Database;
using Content.Shared.Examine;
using Content.Shared.NodeContainer;
using Content.Shared.Power.Generator;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.Server._NF.Power.EntitySystems;

/// <summary>Independent edge ports reuse native pipe networks without bridging them inside the machine.</summary>
public sealed class GeneratorPipingSystem : EntitySystem
{
    [Dependency] private readonly NodeContainerSystem _nodes = default!;
    [Dependency] private readonly AtmosphereSystem _atmosphere = default!;
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesBefore.Add(typeof(GeneratorSystem));
        UpdatesBefore.Add(typeof(NodeGroupSystem));
        SubscribeLocalEvent<GeneratorPipingComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<GeneratorPipingComponent, GeneratorStartAttemptEvent>(OnStartAttempt);
        SubscribeLocalEvent<GeneratorPipingComponent, GeneratorBeforeFuelBurnEvent>(OnBeforeFuelBurn,
            before: new[] { typeof(StationaryGeneratorSystem) });
        SubscribeLocalEvent<GeneratorPipingComponent, ExaminedEvent>(OnExamined);
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<GeneratorPipingComponent>();
        while (query.MoveNext(out var uid, out var piping))
            UpdatePipePorts((uid, piping));
    }

    public void UpdatePipePorts(Entity<GeneratorPipingComponent> entity)
    {
        var connections = new HashSet<(PipeNode Port, PipeNode External)>();
        var transform = Transform(entity);
        if (transform.Anchored && TryComp<MapGridComponent>(transform.GridUid, out var grid))
        {
            var origin = grid.TileIndicesFor(transform.Coordinates);
            foreach (var (name, offset) in entity.Comp.PortOffsets)
            {
                if (!_nodes.TryGetNode(entity.Owner, name, out PipeNode? port) || !port.Connectable(EntityManager))
                    continue;
                var rotated = transform.LocalRotation.RotateVec(new Vector2(offset.X, offset.Y));
                var tile = origin + new Vector2i((int) MathF.Round(rotated.X), (int) MathF.Round(rotated.Y));
                var direction = (entity.Comp.ExhaustNodes.Contains(name) ? PipeDirection.South : PipeDirection.North)
                    .RotatePipeDirection(transform.LocalRotation);
                foreach (var adjacent in grid.GetAnchoredEntities(tile))
                {
                    if (!TryComp<NodeContainerComponent>(adjacent, out var container))
                        continue;
                    foreach (var node in container.Nodes.Values)
                    {
                        if (node is PipeNode external && external.Owner != entity.Owner &&
                            !external.Deleting && external.Connectable(EntityManager) &&
                            external.NodeGroupID == port.NodeGroupID &&
                            external.CurrentPipeLayer == AtmosPipeLayer.Primary &&
                            external.CurrentPipeDirection.HasDirection(direction))
                            connections.Add((port, external));
                    }
                }
            }
        }

        foreach (var previous in entity.Comp.Connections)
        {
            if (connections.Contains(previous))
                continue;
            previous.Port.RemoveAlwaysReachable(previous.External);
            previous.External.RemoveAlwaysReachable(previous.Port);
        }
        foreach (var connection in connections)
        {
            if (entity.Comp.Connections.Contains(connection))
                continue;
            connection.Port.AddAlwaysReachable(connection.External);
            connection.External.AddAlwaysReachable(connection.Port);
        }
        entity.Comp.Connections = connections;
    }

    private void OnShutdown(Entity<GeneratorPipingComponent> entity, ref ComponentShutdown arguments)
    {
        foreach (var connection in entity.Comp.Connections)
        {
            connection.Port.RemoveAlwaysReachable(connection.External);
            connection.External.RemoveAlwaysReachable(connection.Port);
        }
        entity.Comp.Connections.Clear();
    }

    public List<GasMixture> GetConnectedPorts(Entity<GeneratorPipingComponent> entity, bool exhaust)
    {
        UpdatePipePorts(entity);
        var mixtures = new List<GasMixture>();
        foreach (var name in exhaust ? entity.Comp.ExhaustNodes : entity.Comp.IntakeNodes)
        {
            if (!_nodes.TryGetNode(entity.Owner, name, out PipeNode? port) || port.Air.Immutable ||
                !entity.Comp.Connections.Any(connection => connection.Port == port &&
                    port.ReachableNodes.Contains(connection.External) && ReferenceEquals(port.Air, connection.External.Air)))
                continue;
            if (!mixtures.Any(mixture => ReferenceEquals(mixture, port.Air)))
                mixtures.Add(port.Air);
        }
        return mixtures;
    }

    public List<GasMixture> GetIntakeMixtures(EntityUid entity)
    {
        if (TryComp<GeneratorPipingComponent>(entity, out var piping))
        {
            var mixtures = GetConnectedPorts((entity, piping), false);
            // A physically connected but rebuilding/empty network must not draw room air.
            if (mixtures.Count > 0 || piping.Connections.Any(connection =>
                    piping.IntakeNodes.Any(name => _nodes.TryGetNode(entity, name, out PipeNode? node) && node == connection.Port)))
                return mixtures;
        }
        return _atmosphere.GetContainingMixture(entity, false, true) is { Immutable: false } room
            ? new List<GasMixture> { room } : new List<GasMixture>();
    }

    public GasMixture? GetIntakeMixture(EntityUid entity) => GetIntakeMixtures(entity).FirstOrDefault();

    public float GetAvailableOxygen(EntityUid entity) =>
        GetIntakeMixtures(entity).Sum(mixture => mixture.GetMoles(Gas.Oxygen));

    public bool TryGetExhaustMixture(EntityUid entity, [NotNullWhen(true)] out GasMixture? mixture)
    {
        mixture = TryComp<GeneratorPipingComponent>(entity, out var piping)
            ? GetConnectedPorts((entity, piping), true).MinBy(air => air.Pressure) : null;
        return mixture != null;
    }

    public bool ReleaseExhaust(EntityUid entity, GasMixture exhaust)
    {
        if (!TryComp<GeneratorPipingComponent>(entity, out var piping))
        {
            if (_atmosphere.GetContainingMixture(entity, false, true) is not { Immutable: false } room)
                return false;
            _atmosphere.Merge(room, exhaust);
            return true;
        }
        var outlets = GetConnectedPorts((entity, piping), true)
            .Where(mixture => mixture.Pressure < piping.ExhaustShutdownPressure).ToList();
        // A burn already admitted by the safety check must conserve its products even if
        // its unused intake gases just pushed the last outlet above the trip threshold.
        if (outlets.Count == 0)
        {
            if (!TryGetExhaustMixture(entity, out var connected))
                return false;
            outlets.Add(connected);
        }
        var remaining = outlets.Count;
        foreach (var outlet in outlets)
            _atmosphere.Merge(outlet, exhaust.RemoveRatio(1f / remaining--));
        return true;
    }

    public bool TryConsumeIntake(EntityUid entity, float oxygenRequired)
    {
        var intakes = GetIntakeMixtures(entity);
        var available = intakes.Sum(mixture => mixture.GetMoles(Gas.Oxygen));
        if (available < oxygenRequired || available <= 0f)
            return false;
        if (!HasComp<GeneratorPipingComponent>(entity))
        {
            intakes[0].AdjustMoles(Gas.Oxygen, -oxygenRequired);
            return true;
        }
        if (!TryGetExhaustMixture(entity, out var exhaust) ||
            exhaust.Pressure >= Comp<GeneratorPipingComponent>(entity).ExhaustShutdownPressure)
            return false;
        // Equal fractions draw mixed air from each supply, preserving all unused gases.
        // Stage all draws before exhaust so a player-connected intake/exhaust loop cannot consume twice.
        var unused = new GasMixture();
        foreach (var intake in intakes)
        {
            var parcel = intake.RemoveRatio(oxygenRequired / available);
            parcel.SetMoles(Gas.Oxygen, 0f);
            _atmosphere.Merge(unused, parcel);
        }
        if (TryComp<GeneratorExhaustGasComponent>(entity, out var generatorExhaust))
            unused.Temperature = Math.Max(unused.Temperature, generatorExhaust.Temperature);
        ReleaseExhaust(entity, unused);
        return true;
    }

    private void OnStartAttempt(Entity<GeneratorPipingComponent> entity, ref GeneratorStartAttemptEvent arguments)
    {
        if (arguments.FailureMessage != null)
            return;
        if (!TryGetExhaustMixture(entity, out var mixture))
            arguments.FailureMessage = "generator-piping-disconnected";
        else if (mixture.Pressure >= entity.Comp.ExhaustShutdownPressure)
            arguments.FailureMessage = "generator-piping-blocked";
    }

    private void OnBeforeFuelBurn(Entity<GeneratorPipingComponent> entity, ref GeneratorBeforeFuelBurnEvent arguments)
    {
        if (arguments.Cancelled)
            return;
        if (!TryGetExhaustMixture(entity, out var mixture) || mixture.Pressure >= entity.Comp.ExhaustShutdownPressure)
        {
            arguments.Cancelled = true;
            _adminLogger.Add(LogType.Action, LogImpact.Medium,
                $"{ToPrettyString(entity.Owner):subject} shut down because its exhaust was disconnected or above {entity.Comp.ExhaustShutdownPressure} kPa.");
            return;
        }
        arguments.PowerMultiplier = Math.Clamp((entity.Comp.ExhaustShutdownPressure - mixture.Pressure) /
            (entity.Comp.ExhaustShutdownPressure - entity.Comp.ExhaustThrottlePressure), 0.1f, 1f);
        arguments.FuelUsed *= arguments.PowerMultiplier;
    }

    private void OnExamined(Entity<GeneratorPipingComponent> entity, ref ExaminedEvent arguments)
    {
        if (!arguments.IsInDetailsRange)
            return;
        var exhausts = GetConnectedPorts(entity, true);
        if (exhausts.Count == 0)
            arguments.PushMarkup(Loc.GetString("generator-piping-disconnected"));
        foreach (var exhaust in exhausts)
            arguments.PushMarkup(Loc.GetString("generator-piping-pressure", ("pressure", Math.Round(exhaust.Pressure))));
        var intakes = GetConnectedPorts(entity, false);
        if (intakes.Count == 0)
            arguments.PushMarkup(Loc.GetString("generator-piping-intake-disconnected"));
        foreach (var intake in intakes)
            arguments.PushMarkup(Loc.GetString("generator-piping-intake-pressure", ("pressure", Math.Round(intake.Pressure))));
    }
}
