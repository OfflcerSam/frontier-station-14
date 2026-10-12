using System.Linq;
using Content.Server.Power.Components;
using Content.Server.Power.NodeGroups;
using Content.Shared.NodeContainer;
using Content.Shared.Power;

namespace Content.Server.Power.EntitySystems;

internal sealed partial class PowerMonitoringConsoleSystem
{
    // Powerlocks join networks; the simulation does not measure flow through individual cables.
    // Report the connected network's delivered load, without adding it to station totals again.
    private double GetPowerlockNetworkLoad(EntityUid uid, PowerMonitoringDeviceComponent device)
    {
        if (!TryComp<NodeContainerComponent>(uid, out var container) ||
            !container.Nodes.TryGetValue(device.LoadNode, out var node))
            return 0;

        if (node.NodeGroup is ApcNet apc)
            return apc.Providers.SelectMany(p => p.LinkedReceivers).Distinct().Sum(r => r.PowerReceived);

        if (node.NodeGroup is not PowerNet power)
            return 0;

        double load = power.Consumers.Sum(c => c.ReceivedPower);
        foreach (var charger in power.Chargers)
        {
            if (TryComp<PowerNetworkBatteryComponent>(charger.Owner, out var battery))
                load += battery.CurrentReceiving;
        }
        return load;
    }

    private void GetPowerlockApcSources(ApcNet net, List<PowerMonitoringConsoleEntry> sources)
    {
        foreach (var apc in net.Apcs)
        {
            if (TryComp<PowerMonitoringDeviceComponent>(apc.Owner, out var device) &&
                TryComp<PowerNetworkBatteryComponent>(apc.Owner, out var battery))
            {
                sources.Add(new PowerMonitoringConsoleEntry(GetNetEntity(apc.Owner), device.Group,
                    battery.CurrentSupply, GetBatteryLevel(apc.Owner)));
            }
        }
    }

    private void GetPowerlockApcLoads(ApcNet net, List<PowerMonitoringConsoleEntry> loads)
    {
        foreach (var receiver in net.Providers.SelectMany(p => p.LinkedReceivers).Distinct())
        {
            if (TryComp<PowerMonitoringDeviceComponent>(receiver.Owner, out var device))
                loads.Add(new PowerMonitoringConsoleEntry(GetNetEntity(receiver.Owner), device.Group, receiver.PowerReceived));
        }
    }
}
