using Content.Shared._Starlight.Plumbing.Components;
using Content.Shared.Verbs;

namespace Content.Server._NF.Plumbing.EntitySystems;

/// <summary>Controls whether an existing chemistry machine feeds its beaker into a duct.</summary>
public sealed partial class PlumbingOutletValveSystem : EntitySystem
{
    public override void Initialize()
    {
        SubscribeLocalEvent<PlumbingOutletComponent, GetVerbsEvent<AlternativeVerb>>(OnGetValveVerb);
    }

    private void OnGetValveVerb(Entity<PlumbingOutletComponent> entity, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!entity.Comp.AllowValveToggle || !args.CanAccess || !args.CanInteract || !args.CanComplexInteract)
            return;

        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString(entity.Comp.Enabled ? "plumbing-valve-close" : "plumbing-valve-open"),
            Act = () =>
            {
                entity.Comp.Enabled = !entity.Comp.Enabled;
                Dirty(entity);
            },
        });
    }
}
