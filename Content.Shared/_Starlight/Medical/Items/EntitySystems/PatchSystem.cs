using Content.Shared._Starlight.Medical.Items.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Components;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.IdentityManagement;
using Content.Shared.Popups;
using Content.Shared.Chemistry.Reagent;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Audio;
using Robust.Shared.Network;
using Robust.Shared.Timing;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;

namespace Content.Shared._Starlight.Medical.Items.EntitySystems;

public sealed partial class PatchSystem : EntitySystem
{
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private SharedPopupSystem _popupSystem = default!;
    [Dependency] private SharedInteractionSystem _interactionSystem = default!;
    [Dependency] private SharedSolutionContainerSystem _solutionContainerSystem = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private INetManager _netManager = default!;
    [Dependency] private IGameTiming _gameTiming = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PatchComponent, UseInHandEvent>(OnPatchUse);
        SubscribeLocalEvent<PatchComponent, AfterInteractEvent>(OnPatchAfterInteract);
        SubscribeLocalEvent<DamageableComponent, PatchDoAfterEvent>(OnDoAfter);
    }

    private void OnPatchUse(Entity<PatchComponent> ent, ref UseInHandEvent args)
    {
        if (args.Handled)
            return;

        if (TryApply(ent, ent.Comp, args.User, args.User))
            args.Handled = true;
    }

    private void OnPatchAfterInteract(Entity<PatchComponent> ent, ref AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || args.Target == null)
            return;

        if (TryApply(ent, ent.Comp, args.User, args.Target.Value))
            args.Handled = true;
    }

    private bool TryApply(EntityUid patch, PatchComponent patchComponent, EntityUid user, EntityUid target)
    {
        if (!HasComp<DamageableComponent>(target))
            return false;

        if (user != target && !_interactionSystem.InRangeUnobstructed(user, target, popup: true))
            return false;

        _audio.PlayPvs(patchComponent.ApplyBeginSound, patch, AudioParams.Default.WithVolume(1f) with { Variation = 0.125f });

        var isNotSelf = user != target;

        if (isNotSelf)
        {
            var msg = Loc.GetString("medical-item-popup-target", ("user", Identity.Entity(user, EntityManager)), ("item", patch));
            _popupSystem.PopupEntity(msg, target, target, PopupType.Medium);
        }

        var doAfterEventArgs =
            new DoAfterArgs(EntityManager, user, patchComponent.Delay, new PatchDoAfterEvent(), target, target: target, used: patch)
            {
                NeedHand = true,
                BreakOnMove = true,
                BreakOnWeightlessMove = false,
            };

        _doAfter.TryStartDoAfter(doAfterEventArgs);
        return true;
    }

    private void OnDoAfter(Entity<DamageableComponent> entity, ref PatchDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || !_netManager.IsServer ||
            args.Used is not { } patch || TerminatingOrDeleted(patch) || !TryComp(patch, out PatchComponent? patchComponent))
            return;

        args.Handled = true;

        var patchUser = EnsureComp<PatchUserComponent>(entity);
        patchUser.NextUpdateTime = _gameTiming.CurTime + patchUser.Delay;

        // Empty the applied item before queueing deletion so it cannot apply twice in the same tick.
        if (_solutionContainerSystem.TryGetSolution(patch, patchComponent.SolutionContainer, out var solutionEntity, out var solution))
        {
            var dose = _solutionContainerSystem.SplitSolution(solutionEntity.Value, solution.Volume);
            patchUser.ReagentsToInsert.AddRange(dose.Contents);
        }

        _audio.PlayPvs(patchComponent.ApplyEndSound, patch);
        QueueDel(patch);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<PatchUserComponent>();
        while (query.MoveNext(out var uid, out var patchUser))
        {
            if (_gameTiming.CurTime < patchUser.NextUpdateTime)
                continue;

            if (patchUser.ReagentsToInsert.Count == 0)
            {
                RemComp<PatchUserComponent>(uid);
                continue;
            }

            patchUser.NextUpdateTime += patchUser.Delay;

            if (_solutionContainerSystem.TryGetInjectableSolution(uid, out var injectableSolution, out _) && injectableSolution != null)
            {
                for (int i = patchUser.ReagentsToInsert.Count - 1; i >= 0; i--)
                {
                    var reagent = patchUser.ReagentsToInsert[i];
                    var quantityToInsert = FixedPoint2.Min(reagent.Quantity, FixedPoint2.New(patchUser.ReagentInjectAmount));

                    if (reagent.Quantity <= quantityToInsert)
                        patchUser.ReagentsToInsert.RemoveAt(i);
                    else
                    {
                        patchUser.ReagentsToInsert[i] = new ReagentQuantity(reagent.Reagent, reagent.Quantity - quantityToInsert);
                    }

                    var reagentToInject = new ReagentQuantity(reagent.Reagent, quantityToInsert);
                    var solutionToInject = new Solution();
                    solutionToInject.AddReagent(reagentToInject);
                    _solutionContainerSystem.Inject(uid, injectableSolution.Value, solutionToInject);
                }
            }

        }
    }
}
