// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Content.Shared.Verbs;

namespace Content.Server._NF.Chemistry;

public sealed class BottomlessSolutionSystem : EntitySystem
{
    [Dependency] private readonly SharedSolutionContainerSystem _solutionContainers = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<BottomlessSolutionComponent, SolutionContainerChangedEvent>(OnSolutionChanged);
        SubscribeLocalEvent<BottomlessSolutionComponent, GetVerbsEvent<AlternativeVerb>>(OnGetVerbs);
    }

    private void OnSolutionChanged(Entity<BottomlessSolutionComponent> entity, ref SolutionContainerChangedEvent arguments)
    {
        if (entity.Comp.Refilling || arguments.SolutionId != entity.Comp.SolutionName)
            return;

        if (entity.Comp.Sample == null && arguments.Solution.Volume > FixedPoint2.Zero)
            entity.Comp.Sample = arguments.Solution.Clone();

        RefillSolution(entity);
    }

    public void RefillSolution(Entity<BottomlessSolutionComponent> entity)
    {
        if (entity.Comp.Refilling || entity.Comp.Sample is not { } sample || sample.Volume <= FixedPoint2.Zero ||
            !_solutionContainers.TryGetSolution(entity.Owner, entity.Comp.SolutionName, out var fuelSolution, out var solution))
            return;

        var availableVolume = solution.AvailableVolume;
        if (availableVolume <= FixedPoint2.Zero)
            return;

        var refill = sample.Clone();
        refill.ScaleSolution(availableVolume.Float() / sample.Volume.Float());
        entity.Comp.Refilling = true;
        try
        {
            _solutionContainers.AddSolution(fuelSolution.Value, refill);
        }
        finally
        {
            entity.Comp.Refilling = false;
        }
    }

    private void OnGetVerbs(Entity<BottomlessSolutionComponent> entity, ref GetVerbsEvent<AlternativeVerb> arguments)
    {
        if (!arguments.CanAccess || !arguments.CanInteract)
            return;

        var verb = new AlternativeVerb
        {
            Text = Loc.GetString("bottomless-jerry-can-clear-sample"),
            Act = () => ClearSample(entity),
        };
        arguments.Verbs.Add(verb);
    }

    public void ClearSample(Entity<BottomlessSolutionComponent> entity)
    {
        entity.Comp.Refilling = true;
        try
        {
            entity.Comp.Sample = null;
            if (_solutionContainers.TryGetSolution(entity.Owner, entity.Comp.SolutionName, out var fuelSolution))
                _solutionContainers.RemoveAllSolution(fuelSolution.Value);
        }
        finally
        {
            entity.Comp.Refilling = false;
        }
    }
}

