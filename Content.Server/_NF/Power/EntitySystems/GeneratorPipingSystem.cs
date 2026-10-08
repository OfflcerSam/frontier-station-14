// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server._NF.Power.Components;
using Content.Server.Administration.Logs;
using Content.Shared.Atmos;
using Content.Server.Atmos.EntitySystems;
using Content.Server.NodeContainer.EntitySystems;
using Content.Server.NodeContainer.Nodes;
using Content.Server.Power.Generator;
using Content.Shared.Database;
using Content.Shared.Examine;
using Content.Shared.Power.Generator;

namespace Content.Server._NF.Power.EntitySystems;

/// <summary>Reuses atmos pipe networks; never redirects a required but blocked exhaust into the room.</summary>
public sealed class GeneratorPipingSystem : EntitySystem
{
    [Dependency] private readonly NodeContainerSystem _nodes = default!;
    [Dependency] private readonly AtmosphereSystem _atmosphere = default!;
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GeneratorPipingComponent, GeneratorStartAttemptEvent>(OnStartAttempt);
        SubscribeLocalEvent<GeneratorPipingComponent, GeneratorBeforeFuelBurnEvent>(OnBeforeFuelBurn,
            before: new[] { typeof(StationaryGeneratorSystem) });
        SubscribeLocalEvent<GeneratorPipingComponent, ExaminedEvent>(OnExamined);
    }

    public GasMixture? GetIntakeMixture(EntityUid entity)
    {
        if (TryComp<GeneratorPipingComponent>(entity, out var piping) &&
            _nodes.TryGetNode(entity, piping.IntakeNode, out PipeNode? intake) &&
            intake.ReachableNodes.Any(node => node.Owner != entity && node.Connectable(EntityManager)))
            return intake.Air.Immutable ? null : intake.Air;
        return _atmosphere.GetContainingMixture(entity, false, true);
    }

    public bool TryGetExhaustMixture(EntityUid entity, [NotNullWhen(true)] out GasMixture? mixture)
    {
        mixture = null;
        if (!Transform(entity).Anchored || !TryComp<GeneratorPipingComponent>(entity, out var piping) ||
            !_nodes.TryGetNode(entity, piping.ExhaustNode, out PipeNode? exhaust) ||
            !exhaust.ReachableNodes.Any(node => node.Owner != entity && node.Connectable(EntityManager)) ||
            exhaust.Air.Immutable)
            return false;
        mixture = exhaust.Air;
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
        arguments.PushMarkup(TryGetExhaustMixture(entity, out var mixture)
            ? Loc.GetString("generator-piping-pressure", ("pressure", Math.Round(mixture.Pressure)))
            : Loc.GetString("generator-piping-disconnected"));
    }
}
