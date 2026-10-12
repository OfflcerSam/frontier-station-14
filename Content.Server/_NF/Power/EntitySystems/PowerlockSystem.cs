using Content.Server._NF.Power.Components;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Power.NodeGroups;
using Content.Server.Shuttles.Events;
using Content.Shared._NF.Power;
using Content.Shared.NodeContainer;
using Content.Shared.Construction.Components;
using Content.Shared.Examine;
using Content.Shared.Power;
using Content.Shared.NodeContainer.NodeGroups;

namespace Content.Server._NF.Power.EntitySystems;

public sealed partial class PowerlockSystem : EntitySystem
{
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] private readonly PowerNetSystem _powerNet = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PowerlockComponent, DockEvent>(OnDock);
        SubscribeLocalEvent<PowerlockComponent, UndockEvent>(OnUndock);
        SubscribeLocalEvent<PowerlockComponent, ExaminedEvent>(OnExamine);
        UpdatesAfter.Add(typeof(PowerNetSystem));
        UpdatesBefore.Add(typeof(BatteryInterfaceSystem));
    }

    public NetworkPowerStatistics GetNetworkStatistics(EntityUid uid, string nodeName = "power")
    {
        if (!Transform(uid).Anchored || !TryComp<NodeContainerComponent>(uid, out var container) ||
            !container.Nodes.TryGetValue(nodeName, out var node))
            return default;

        return node.NodeGroup switch
        {
            PowerNet net => _powerNet.GetNetworkStatistics(net.NetworkNode),
            ApcNet net => _powerNet.GetNetworkStatistics(net.NetworkNode),
            _ => default,
        };
    }

    private void OnExamine(Entity<PowerlockComponent> ent, ref ExaminedEvent args)
    {
        var powered = GetNetworkStatistics(ent).SupplyCurrent > 0 || Comp<BatteryComponent>(ent).CurrentCharge > 0;
        // Keep operating status first, followed by the standard connector, anchoring,
        // device address and maintenance-panel examines supplied by their own systems.
        args.PushMarkup(Loc.GetString("power-receiver-component-on-examine-main",
            ("stateText", Loc.GetString(powered ? "power-receiver-component-on-examine-powered" :
                "power-receiver-component-on-examine-unpowered"))), priority: 2);
        var battery = Comp<PowerNetworkBatteryComponent>(ent);
        args.PushMarkup(Loc.GetString(battery.CanCharge ? "powerlock-input-on" : "powerlock-input-off"), priority: 1);
        args.PushMarkup(Loc.GetString(battery.CanDischarge ? "powerlock-output-on" : "powerlock-output-off"), priority: 1);
        if (args.IsInDetailsRange && TryComp<NodeContainerComponent>(ent, out var nodes) &&
            nodes.Nodes.TryGetValue("power", out var node))
        {
            var key = node.NodeGroupID switch
            {
                NodeGroupID.HVPower => "node-container-component-on-examine-details-hvpower",
                NodeGroupID.MVPower => "node-container-component-on-examine-details-mvpower",
                _ => "node-container-component-on-examine-details-apc",
            };
            args.PushMarkup(Loc.GetString(key));
        }
        if (!HasComp<AnchorableComponent>(ent))
            args.PushMarkup(Loc.GetString(Transform(ent).Anchored ? "examinable-anchored" : "examinable-unanchored", ("target", ent.Owner)));
    }

    private void OnDock(Entity<PowerlockComponent> ent, ref DockEvent args)
    {
        Refresh(ent, true);
    }

    private void OnUndock(Entity<PowerlockComponent> ent, ref UndockEvent args)
    {
        Refresh(ent, false);
    }

    private void Refresh(Entity<PowerlockComponent> ent, bool docked)
    {
        _appearance.SetData(ent, PowerlockVisuals.Docked, docked);
    }
}
