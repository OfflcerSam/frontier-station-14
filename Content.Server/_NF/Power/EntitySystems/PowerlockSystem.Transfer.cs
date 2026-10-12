using System.Linq;
using Content.Server._NF.Power.Components;
using Content.Server.NodeContainer.EntitySystems;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Power.NodeGroups;
using Content.Server.Shuttles.Components;
using Content.Shared.NodeContainer;

namespace Content.Server._NF.Power.EntitySystems;

public sealed partial class PowerlockSystem
{
    [Dependency] private readonly BatterySystem _batteries = default!;

    // The standard power solver requires an acyclic network of battery connections.
    // Keep each port on its own cable network and conserve energy explicitly across the dock.
    // Local cable use has priority; dock transfer shares the same input/output rate budgets.
    public void Prepare(float frameTime)
    {
        if (frameTime <= 0)
            return;

        var budgets = new Dictionary<IBasePowerNet, (float Balance, float Requested)>();
        var query = EntityQueryEnumerator<PowerlockComponent, BatteryComponent, PowerNetworkBatteryComponent>();
        while (query.MoveNext(out var uid, out _, out var storage, out var settings))
        {
            var load = Comp<PowerConsumerComponent>(uid);
            var supply = Comp<PowerSupplierComponent>(uid);
            load.DrawRate = 0;
            supply.Enabled = false;
            supply.MaxSupply = 0;
            if (!settings.Enabled || !Transform(uid).Anchored || Paused(uid))
                continue;

            var node = Comp<NodeContainerComponent>(uid).Nodes["power"];
            if (node.NodeGroup is not IBasePowerNet network)
                continue;
            if (!budgets.TryGetValue(network, out var budget))
            {
                budget.Balance = network switch
                {
                    PowerNet net => ExternalBalance(net, frameTime),
                    ApcNet net => ExternalBalance(net, frameTime),
                    _ => 0,
                };
            }
            var balance = budget.Balance;
            if (balance > 0 && settings.CanCharge)
                load.DrawRate = Math.Min(balance, Math.Min(settings.MaxChargeRate,
                    (storage.MaxCharge - storage.CurrentCharge) / (Math.Max(settings.Efficiency, 0.001f) * frameTime)));
            if (balance < 0 && settings.CanDischarge)
            {
                supply.Enabled = true;
                supply.MaxSupply = Math.Min(-balance, Math.Min(settings.MaxSupply, storage.CurrentCharge / frameTime));
                supply.SupplyRampTolerance = settings.SupplyRampTolerance;
                supply.SupplyRampRate = settings.SupplyRampRate;
            }
            budget.Requested += load.DrawRate + supply.MaxSupply;
            budgets[network] = budget;
        }

        // Several ports can share one rear network. Split its available surplus/deficit
        // between them so charging buffers cannot steal power from the existing loads.
        var ports = EntityQueryEnumerator<PowerlockComponent, PowerConsumerComponent, PowerSupplierComponent>();
        while (ports.MoveNext(out var uid, out _, out var load, out var supply))
        {
            if (Comp<NodeContainerComponent>(uid).Nodes["power"].NodeGroup is not IBasePowerNet net ||
                !budgets.TryGetValue(net, out var budget) || budget.Requested <= Math.Abs(budget.Balance))
                continue;
            var share = Math.Abs(budget.Balance) / budget.Requested;
            load.DrawRate *= share;
            supply.MaxSupply *= share;
        }
    }

    private float ExternalBalance<TNet>(BasePowerNet<TNet> net, float frameTime) where TNet : IBasePowerNet
    {
        // Exclude all powerlock buffers: they must not charge one another through the local cable.
        var supply = net.Suppliers.Where(s => !HasComp<PowerlockComponent>(s.Owner) && s.Enabled && !s.NetworkSupply.Paused)
            .Sum(s => Math.Min(s.MaxSupply, s.SupplyRampPosition + s.SupplyRampTolerance));
        var demand = net.Consumers.Where(c => !HasComp<PowerlockComponent>(c.Owner) && c.NetworkLoad.Enabled && !c.NetworkLoad.Paused)
            .Sum(c => c.DrawRate);
        if (net is PowerNet power)
        {
            foreach (var charger in power.Chargers)
            {
                var battery = Comp<PowerNetworkBatteryComponent>(charger.Owner);
                var storage = Comp<BatteryComponent>(charger.Owner);
                if (battery.Enabled && battery.CanCharge && !Paused(charger.Owner))
                    demand += Math.Min(battery.MaxChargeRate,
                        (storage.MaxCharge - storage.CurrentCharge) / (Math.Max(battery.Efficiency, 0.001f) * frameTime));
            }
            foreach (var discharger in power.Dischargers)
                supply += AvailableBatterySupply(discharger.Owner, frameTime);
        }
        if (net is ApcNet apc)
        {
            demand += apc.Providers.SelectMany(p => p.LinkedReceivers).Distinct()
                .Where(r => r.NetworkLoad.Enabled && !r.NetworkLoad.Paused).Sum(r => r.Load);
            foreach (var source in apc.Apcs)
                supply += AvailableBatterySupply(source.Owner, frameTime);
        }
        return supply - demand;
    }

    private float AvailableBatterySupply(EntityUid uid, float frameTime)
    {
        var battery = Comp<PowerNetworkBatteryComponent>(uid);
        return battery.Enabled && battery.CanDischarge && !Paused(uid)
            ? Math.Min(Math.Min(battery.MaxSupply, battery.SupplyRampPosition + battery.SupplyRampTolerance),
                Comp<BatteryComponent>(uid).CurrentCharge / frameTime) : 0;
    }

    public override void Update(float frameTime)
    {
        if (frameTime <= 0)
            return;

        var query = EntityQueryEnumerator<PowerlockComponent, BatteryComponent, PowerNetworkBatteryComponent>();
        while (query.MoveNext(out var uid, out var port, out var storage, out var settings))
        {
            if (Paused(uid))
                continue;
            port.Input = Comp<PowerConsumerComponent>(uid).ReceivedPower;
            port.Output = Comp<PowerSupplierComponent>(uid).CurrentSupply;
            _batteries.SetCharge(uid, storage.CurrentCharge + (port.Input * settings.Efficiency - port.Output) * frameTime, storage);
        }

        var visited = new HashSet<EntityUid>();
        var docks = EntityQueryEnumerator<PowerlockComponent, DockingComponent, BatteryComponent>();
        while (docks.MoveNext(out var uid, out _, out var dock, out var a))
        {
            if (visited.Contains(uid) || dock.DockedWith is not { } other || Paused(uid) || Paused(other) ||
                !Transform(uid).Anchored || !Transform(other).Anchored ||
                !HasComp<PowerlockComponent>(other) || !TryComp<BatteryComponent>(other, out var b))
                continue;
            visited.Add(uid);
            visited.Add(other);
            if (a.MaxCharge <= 0 || b.MaxCharge <= 0 ||
                Comp<NodeContainerComponent>(uid).Nodes["power"].NodeGroupID != Comp<NodeContainerComponent>(other).Nodes["power"].NodeGroupID)
                continue;

            // Move only enough to equalize charge fractions; never overshoot and ping-pong energy.
            var difference = (float) (((double) a.CurrentCharge * b.MaxCharge - (double) b.CurrentCharge * a.MaxCharge) / (a.MaxCharge + b.MaxCharge));
            if (difference > 0)
                Transfer(uid, other, difference, frameTime);
            else if (difference < 0)
                Transfer(other, uid, -difference, frameTime);
        }

        var readings = EntityQueryEnumerator<PowerlockComponent, PowerNetworkBatteryComponent>();
        while (readings.MoveNext(out _, out var port, out var settings))
        {
            settings.CurrentReceiving = port.Input;
            settings.CurrentSupply = port.Output;
        }
    }

    private void Transfer(EntityUid from, EntityUid to, float energy, float frameTime)
    {
        var source = Comp<PowerNetworkBatteryComponent>(from);
        var target = Comp<PowerNetworkBatteryComponent>(to);
        if (!source.Enabled || !target.Enabled || !source.CanDischarge || !target.CanCharge || target.Efficiency <= 0)
            return;
        var a = Comp<BatteryComponent>(from);
        var b = Comp<BatteryComponent>(to);
        var sourcePort = Comp<PowerlockComponent>(from);
        var targetPort = Comp<PowerlockComponent>(to);
        energy = Math.Min(energy, Math.Max(0, source.MaxSupply - sourcePort.Output) * frameTime);
        energy = Math.Min(energy, Math.Max(0, target.MaxChargeRate - targetPort.Input) * frameTime);
        energy = Math.Min(energy, Math.Min(a.CurrentCharge, (b.MaxCharge - b.CurrentCharge) / target.Efficiency));
        if (energy <= 0)
            return;
        _batteries.SetCharge(from, a.CurrentCharge - energy, a);
        _batteries.SetCharge(to, b.CurrentCharge + energy * target.Efficiency, b);
        sourcePort.Output += energy / frameTime;
        targetPort.Input += energy / frameTime;
    }
}

/// <summary>Prepare powerlock loads/supplies before the solver; apply the resulting energy afterwards.</summary>
public sealed class PowerlockPrepareSystem : EntitySystem
{
    [Dependency] private readonly PowerlockSystem _powerlocks = default!;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesAfter.Add(typeof(NodeGroupSystem));
        UpdatesBefore.Add(typeof(PowerNetSystem));
    }

    public override void Update(float frameTime) => _powerlocks.Prepare(frameTime);
}
