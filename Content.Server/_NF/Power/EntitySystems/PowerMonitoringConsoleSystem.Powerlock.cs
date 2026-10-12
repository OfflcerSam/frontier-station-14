using System.Linq;
using Content.Server.Power.Components;
using Content.Server.Power.NodeGroups;
using Content.Shared.Power;

namespace Content.Server.Power.EntitySystems;

internal sealed partial class PowerMonitoringConsoleSystem
{
    [Dependency] private readonly Content.Server._NF.Power.EntitySystems.PowerlockSystem _powerlock = default!;

    private void GetPowerlockApcSources(EntityUid uid, ApcNet net, List<PowerMonitoringConsoleEntry> sources)
    {
        foreach (var supplier in net.Suppliers)
        {
            if (supplier.Owner == uid)
                continue;
            if (TryComp<PowerMonitoringDeviceComponent>(supplier.Owner, out var device))
                sources.Add(new PowerMonitoringConsoleEntry(GetNetEntity(supplier.Owner), device.Group,
                    supplier.CurrentSupply, GetBatteryLevel(supplier.Owner)));
        }
        foreach (var apc in net.Apcs)
        {
            if (apc.Owner == uid)
                continue;
            if (TryComp<PowerMonitoringDeviceComponent>(apc.Owner, out var device) &&
                TryComp<PowerNetworkBatteryComponent>(apc.Owner, out var battery))
            {
                sources.Add(new PowerMonitoringConsoleEntry(GetNetEntity(apc.Owner), device.Group,
                    battery.CurrentSupply, GetBatteryLevel(apc.Owner)));
            }
        }
    }

    private void GetPowerlockApcLoads(EntityUid uid, ApcNet net, List<PowerMonitoringConsoleEntry> loads)
    {
        foreach (var consumer in net.Consumers)
        {
            if (consumer.Owner == uid)
                continue;
            if (TryComp<PowerMonitoringDeviceComponent>(consumer.Owner, out var device))
                loads.Add(new PowerMonitoringConsoleEntry(GetNetEntity(consumer.Owner), device.Group,
                    consumer.ReceivedPower, GetBatteryLevel(consumer.Owner)));
        }
        foreach (var receiver in net.Providers.SelectMany(p => p.LinkedReceivers).Distinct())
        {
            if (receiver.Owner == uid)
                continue;
            if (TryComp<PowerMonitoringDeviceComponent>(receiver.Owner, out var device))
                loads.Add(new PowerMonitoringConsoleEntry(GetNetEntity(receiver.Owner), device.Group, receiver.PowerReceived));
        }
    }
}
