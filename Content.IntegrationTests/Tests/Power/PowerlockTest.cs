using System.Linq;
using System.Numerics;
using Content.Server._NF.Power.EntitySystems;
using Content.Server.NodeContainer.EntitySystems;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.Examine;
using Content.Shared.NodeContainer;
using Content.Shared.Power;
using Content.Shared.Wires;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests.Power;

[TestFixture]
public sealed class PowerlockTest
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  parent: ComputerPowerMonitoring
  id: PowerlockTestMonitor
  components:
  - type: UserInterface
    interfaces:
      enum.PowerMonitoringConsoleUiKey.Key:
        type: PowerMonitoringConsoleBoundUserInterface
        requireInputValidation: false
";

    [Test]
    public async Task RearTerminalOnly(
        [Values("PowerlockLV", "PowerlockMV", "PowerlockHV", "PowerlockLVFrame", "PowerlockMVFrame", "PowerlockHVFrame")] string prototype,
        [Values(0, 90, 180, 270)] int degrees)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var em = server.ResolveDependency<IEntityManager>();
        await server.WaitAssertion(() =>
        {
            var grid = server.ResolveDependency<IMapManager>().CreateGridEntity(map.MapId);
            var maps = em.System<SharedMapSystem>();
            var transforms = em.System<SharedTransformSystem>();
            var groups = em.System<NodeGroupSystem>();
            var offsets = new[] { Vector2i.Zero, new Vector2i(0, 1), new Vector2i(0, -1), new Vector2i(1, 0), new Vector2i(-1, 0) };
            foreach (var offset in offsets)
                maps.SetTile(grid.Owner, grid.Comp, offset, new Tile(1));
            var port = em.SpawnEntity(prototype, new EntityCoordinates(grid.Owner, new Vector2(0.5f, 0.5f)));
            transforms.SetLocalRotation(port, Angle.FromDegrees(degrees));
            var transform = em.GetComponent<TransformComponent>(port);
            if (!transform.Anchored)
                transforms.AnchorEntity(port, transform);
            var node = em.GetComponent<NodeContainerComponent>(port).Nodes["power"];
            var rear = degrees switch
            {
                0 => new Vector2i(0, 1),
                90 => new Vector2i(-1, 0),
                180 => new Vector2i(0, -1),
                _ => new Vector2i(1, 0),
            };
            var cableProto = prototype.Contains("LV") ? "CableApcExtension" : prototype.Contains("MV") ? "CableMV" : "CableHV";
            foreach (var offset in offsets)
            {
                var pos = new EntityCoordinates(grid.Owner, new Vector2(offset.X + 0.5f, offset.Y + 0.5f));
                var cable = em.SpawnEntity(cableProto, pos);
                var cableNode = em.GetComponent<NodeContainerComponent>(cable).Nodes["power"];
                groups.ForceUpdate();
                Assert.That(node.NodeGroup, Is.Not.SameAs(cableNode.NodeGroup), "A bare cable must not bypass the terminal.");
                var terminal = em.SpawnEntity("CableTerminal", pos);
                var facing = offset.X == 1 ? 270 : offset.X == -1 ? 90 : offset.Y == -1 ? 180 : 0;
                transforms.SetLocalRotation(terminal, Angle.FromDegrees(facing));
                groups.ForceUpdate();
                Assert.That(node.NodeGroup == cableNode.NodeGroup, Is.EqualTo(offset == rear), $"{prototype}, {degrees}, terminal {offset}");
                if (offset == rear)
                {
                    transforms.SetLocalRotation(port, Angle.FromDegrees(degrees + 90));
                    groups.ForceUpdate();
                    Assert.That(node.NodeGroup, Is.Not.SameAs(cableNode.NodeGroup));
                    transforms.SetLocalRotation(port, Angle.FromDegrees(degrees));
                    groups.ForceUpdate();
                    Assert.That(node.NodeGroup, Is.SameAs(cableNode.NodeGroup));
                }
                em.DeleteEntity(terminal);
                em.DeleteEntity(cable);
                groups.ForceUpdate();
                Assert.That(node.ReachableNodes, Is.Empty);
            }
            em.DeleteEntity(grid.Owner);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("PowerlockLV", "CableApcExtension", "PowerlockLV")]
    [TestCase("PowerlockLV", "CableApcExtension", "PowerlockLVFrame")]
    [TestCase("PowerlockMV", "CableMV", "PowerlockMV")]
    [TestCase("PowerlockMV", "CableMV", "PowerlockMVFrame")]
    [TestCase("PowerlockHV", "CableHV", "PowerlockHV")]
    [TestCase("PowerlockHV", "CableHV", "PowerlockHVFrame")]
    public async Task BufferedDockingLifecycle(string prototype, string cablePrototype, string partnerPrototype)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var em = server.ResolveDependency<IEntityManager>();
        await server.WaitAssertion(() =>
        {
            var maps = server.ResolveDependency<IMapManager>();
            var gridA = maps.CreateGridEntity(map.MapId);
            var gridB = maps.CreateGridEntity(map.MapId);
            var transforms = em.System<SharedTransformSystem>();
            var groups = em.System<NodeGroupSystem>();
            var docking = em.System<DockingSystem>();
            var mapSystem = em.System<SharedMapSystem>();
            transforms.SetLocalPosition(gridB.Owner, new Vector2(0, -1));
            mapSystem.SetTile(gridA.Owner, gridA.Comp, Vector2i.Zero, new Tile(1));
            mapSystem.SetTile(gridB.Owner, gridB.Comp, Vector2i.Zero, new Tile(1));
            mapSystem.SetTile(gridA.Owner, gridA.Comp, new Vector2i(0, 1), new Tile(1));
            mapSystem.SetTile(gridB.Owner, gridB.Comp, new Vector2i(0, -1), new Tile(1));
            var posA = new EntityCoordinates(gridA.Owner, new Vector2(0.5f, 0.5f));
            var posB = new EntityCoordinates(gridB.Owner, new Vector2(0.5f, 0.5f));
            var cablePosA = new EntityCoordinates(gridA.Owner, new Vector2(0.5f, 1.5f));
            var cablePosB = new EntityCoordinates(gridB.Owner, new Vector2(0.5f, -0.5f));
            var cableA = em.SpawnEntity(cablePrototype, cablePosA);
            var cableB = em.SpawnEntity(cablePrototype, cablePosB);
            em.SpawnEntity("CableTerminal", cablePosA);
            var terminalB = em.SpawnEntity("CableTerminal", cablePosB);
            transforms.SetLocalRotation(terminalB, Angle.FromDegrees(180));
            var portA = em.SpawnEntity(prototype, posA);
            var portB = em.SpawnEntity(partnerPrototype, posB);
            if (partnerPrototype.EndsWith("Frame"))
            {
                Assert.That(em.GetComponent<TransformComponent>(portB).Anchored, Is.False);
                Assert.That(em.GetComponent<DockingComponent>(portB).ReceiveOnly, Is.True);
                transforms.AnchorEntity(portB, em.GetComponent<TransformComponent>(portB));
            }
            transforms.SetLocalRotation(portB, Angle.FromDegrees(180));
            var nodesA = em.GetComponent<NodeContainerComponent>(portA).Nodes;
            var nodesB = em.GetComponent<NodeContainerComponent>(portB).Nodes;
            var wireA = em.GetComponent<NodeContainerComponent>(cableA).Nodes["power"];
            var wireB = em.GetComponent<NodeContainerComponent>(cableB).Nodes["power"];
            var dockA = em.GetComponent<DockingComponent>(portA);
            var dockB = em.GetComponent<DockingComponent>(portB);
            var batteryA = em.GetComponent<PowerNetworkBatteryComponent>(portA);
            var batteryB = em.GetComponent<PowerNetworkBatteryComponent>(portB);
            var storageA = em.GetComponent<BatteryComponent>(portA);
            var storageB = em.GetComponent<BatteryComponent>(portB);
            var expectedCapacity = prototype.Contains("LV") ? 50000 : 2500000;
            Assert.That(storageA.MaxCharge, Is.EqualTo(expectedCapacity));
            Assert.That(storageB.MaxCharge, Is.EqualTo(expectedCapacity));
            var actor = em.SpawnEntity("MobHuman", posA);
            void Send(EntityUid port, BoundUserInterfaceMessage message)
            {
                message.Actor = actor;
                message.UiKey = BatteryUiKey.Key;
                em.EventBus.RaiseLocalEvent(port, (object) message);
                groups.ForceUpdate();
            }
            void CheckExamine(EntityUid port, bool anchored = true)
            {
                var examine = new ExaminedEvent(new FormattedMessage(), port, actor, true, false);
                em.EventBus.RaiseLocalEvent(port, examine);
                var text = examine.GetTotalMessage().ToString();
                var battery = em.GetComponent<PowerNetworkBatteryComponent>(port);
                var address = em.GetComponent<DeviceNetworkComponent>(port).Address;
                Assert.That(address, Does.StartWith("PWL-"));
                var lines = new[]
                {
                    "It appears to be",
                    battery.CanCharge ? "The input breaker is on." : "The input breaker is off.",
                    battery.CanDischarge ? "The output breaker is on." : "The output breaker is off.",
                    "It has a connector for " + prototype.Substring("Powerlock".Length) + " cables.",
                    anchored ? "It is anchored to the floor." : "It is unanchored from the floor.",
                    "The device's address is " + address + ".",
                    em.GetComponent<WiresPanelComponent>(port).Open ? "The maintenance panel is open." : "The maintenance panel is closed.",
                };
                var last = -1;
                foreach (var line in lines)
                {
                    var index = text.IndexOf(line, StringComparison.Ordinal);
                    Assert.That(index, Is.GreaterThan(last), text);
                    Assert.That(text.LastIndexOf(line, StringComparison.Ordinal), Is.EqualTo(index), "Duplicate examine: " + text);
                    last = index;
                }
            }
            groups.ForceUpdate();
            CheckExamine(portA);
            CheckExamine(portB);
            em.System<SharedWiresSystem>().TogglePanel(portA, em.GetComponent<WiresPanelComponent>(portA), true);
            CheckExamine(portA);
            em.System<SharedWiresSystem>().TogglePanel(portA, em.GetComponent<WiresPanelComponent>(portA), false);
            Assert.That(nodesA["power"].NodeGroup, Is.SameAs(wireA.NodeGroup));
            Assert.That(nodesB["power"].NodeGroup, Is.SameAs(wireB.NodeGroup));
            docking.Dock((portA, dockA), (portB, dockB));
            groups.ForceUpdate();
            Assert.That(wireA.NodeGroup, Is.Not.SameAs(wireB.NodeGroup), "The buffer must not be bypassed by a direct cable bridge.");

            var voltage = (Voltage) nodesA["power"].NodeGroupID;
            var loadA = em.EnsureComponent<PowerConsumerComponent>(cableA);
            var loadB = em.EnsureComponent<PowerConsumerComponent>(cableB);
            loadA.Voltage = loadB.Voltage = voltage;
            var solver = em.System<PowerNetSystem>();
            var powerlocks = em.System<PowerlockSystem>();
            void Solve(float seconds = 1f)
            {
                powerlocks.Prepare(seconds);
                solver.Update(seconds);
                powerlocks.Update(seconds);
                solver.Validate();
            }
            var batteries = em.System<BatterySystem>();
            foreach (var reverse in new[] { false, true })
            {
                var source = reverse ? portB : portA;
                var target = reverse ? portA : portB;
                var buffer = reverse ? batteryB : batteryA;
                var receivingLoad = reverse ? loadA : loadB;
                loadA.DrawRate = reverse ? 7000 : 0;
                loadB.DrawRate = reverse ? 0 : 7000;
                batteries.SetCharge(portA, reverse ? 0 : 20000);
                batteries.SetCharge(portB, reverse ? 20000 : 0);
                Send(source, new BatterySetDischargeRateMessage(5000));
                Send(target, new BatterySetDischargeRateMessage(5000));
                Solve();
                Assert.That(storageA.CurrentCharge + storageB.CurrentCharge, Is.EqualTo(20000).Within(0.1f), "Dock transfer must conserve energy.");
                Solve();
                Assert.That(receivingLoad.ReceivedPower, Is.GreaterThan(0).And.LessThanOrEqualTo(5000));
                Assert.That(buffer.CurrentSupply, Is.GreaterThan(0).And.LessThanOrEqualTo(5000));
                Assert.That(storageA.CurrentCharge + storageB.CurrentCharge, Is.LessThan(20000), "Transfer must consume stored energy rather than create it.");
                for (var tick = 0; tick < 10; tick++)
                {
                    var before = storageA.CurrentCharge + storageB.CurrentCharge;
                    Solve(1f / 30);
                    var consumed = (loadA.ReceivedPower + loadB.ReceivedPower) / 30;
                    Assert.That(storageA.CurrentCharge + storageB.CurrentCharge, Is.EqualTo(before - consumed).Within(0.1f));
                    Assert.That(batteryA.CurrentSupply, Is.LessThanOrEqualTo(5000.1f));
                    Assert.That(batteryB.CurrentSupply, Is.LessThanOrEqualTo(5000.1f));
                }
                Send(target, new BatterySetInputBreakerMessage(false));
                Solve(1f / 30);
                Assert.That(receivingLoad.ReceivedPower, Is.GreaterThan(0), "Disabling input must still allow stored charge to supply the local network.");
                batteries.SetCharge(target, 0);
                Solve();
                Assert.That(receivingLoad.ReceivedPower, Is.Zero);
                Assert.That(em.GetComponent<PowerNetworkBatteryComponent>(target).CurrentReceiving, Is.Zero);
                CheckExamine(target);
                Send(target, new BatterySetInputBreakerMessage(true));
                Send(source, new BatterySetOutputBreakerMessage(false));
                Solve();
                Assert.That(receivingLoad.ReceivedPower, Is.Zero);
                Send(source, new BatterySetOutputBreakerMessage(true));
                batteries.SetCharge(source, 20000);
                Send(target, new BatterySetOutputBreakerMessage(false));
                Solve();
                Assert.That(em.GetComponent<BatteryComponent>(target).CurrentCharge, Is.GreaterThan(0));
                Assert.That(receivingLoad.ReceivedPower, Is.Zero, "Disabling output must still allow the buffer to charge.");
                Send(target, new BatterySetOutputBreakerMessage(true));
                Solve();
                Assert.That(receivingLoad.ReceivedPower, Is.GreaterThan(0));
            }

            // Input limit and storage work even while undocked.
            docking.Undock((portA, dockA));
            groups.ForceUpdate();
            loadA.DrawRate = loadB.DrawRate = 0;
            batteries.SetCharge(portA, 0);
            var supply = em.EnsureComponent<PowerSupplierComponent>(cableA);
            supply.Voltage = voltage;
            supply.MaxSupply = 20000;
            supply.Enabled = true;
            supply.SupplyRampTolerance = 20000;
            var limits = em.GetComponent<BatteryInterfaceComponent>(portA);
            Send(portA, new BatterySetChargeRateMessage(float.MaxValue));
            Send(portA, new BatterySetDischargeRateMessage(float.MaxValue));
            Assert.That(batteryA.MaxChargeRate, Is.EqualTo(limits.MaxChargeRate));
            Assert.That(batteryA.MaxSupply, Is.EqualTo(limits.MaxSupply));
            Send(portA, new BatterySetDischargeRateMessage(-1));
            Assert.That(batteryA.MaxSupply, Is.EqualTo(limits.MinSupply));
            Send(portA, new BatterySetChargeRateMessage(5000));
            Solve();
            Assert.That(batteryA.CurrentReceiving, Is.GreaterThan(0).And.LessThanOrEqualTo(5000));
            Assert.That(storageA.CurrentCharge, Is.GreaterThan(0));
            Send(portA, new BatterySetInputBreakerMessage(false));
            Solve();
            Assert.That(batteryA.CurrentReceiving, Is.Zero);
            Send(portA, new BatterySetInputBreakerMessage(true));
            batteries.SetCharge(portA, expectedCapacity - 1);
            Solve();
            Assert.That(storageA.CurrentCharge, Is.EqualTo(expectedCapacity));
            Assert.That(batteryA.CurrentReceiving, Is.LessThanOrEqualTo(1));
            batteries.SetCharge(portA, 10000);

            var ui = em.System<SharedUserInterfaceSystem>();
            Assert.That(ui.TryOpenUi(portA, BatteryUiKey.Key, actor), Is.True);
            em.System<BatteryInterfaceSystem>().Update(1f);
            Assert.That(ui.TryGetUiState<BatteryBuiState>(portA, BatteryUiKey.Key, out var panel), Is.True);
            Assert.That(panel!.Capacity, Is.EqualTo(expectedCapacity));
            Assert.That(panel.MaxChargeRate, Is.EqualTo(5000));
            Assert.That(panel.SupplyingNetworkHasPower, Is.True);
            var monitor = em.SpawnEntity("PowerlockTestMonitor", posA);
            Assert.That(ui.TryOpenUi(monitor, PowerMonitoringConsoleUiKey.Key, actor), Is.True);
            em.EventBus.RaiseLocalEvent(monitor, new PowerMonitoringConsoleMessage(em.GetNetEntity(portA), PowerMonitoringConsoleGroup.Powerlock));
            Solve();
            em.System<PowerMonitoringConsoleSystem>().Update(2f);
            Assert.That(ui.TryGetUiState<PowerMonitoringConsoleBoundInterfaceState>(monitor, PowerMonitoringConsoleUiKey.Key, out var readings), Is.True);
            var entry = readings!.AllEntries.Single();
            Assert.That(entry.PowerlockDocked, Is.False);
            Assert.That(entry.PowerlockSupply, Is.GreaterThan(0));
            Assert.That(entry.PowerlockDemand, Is.GreaterThan(0));
            Assert.That(entry.BatteryLevel, Is.GreaterThan(0));
            em.DeleteEntity(monitor);

            // Two buffers on the same terminal must share surplus rather than starve other loads.
            var extraPort = em.SpawnEntity(prototype, posA);
            groups.ForceUpdate();
            batteries.SetCharge(portA, 0);
            supply.MaxSupply = 5000;
            loadA.DrawRate = 2000;
            Solve();
            Assert.That(loadA.ReceivedPower, Is.EqualTo(2000).Within(0.1f));
            Assert.That(batteryA.CurrentReceiving, Is.EqualTo(1500).Within(0.1f));
            Assert.That(em.GetComponent<PowerNetworkBatteryComponent>(extraPort).CurrentReceiving, Is.EqualTo(1500).Within(0.1f));
            em.DeleteEntity(extraPort);
            groups.ForceUpdate();
            supply.MaxSupply = 20000;
            loadA.DrawRate = 0;

            docking.Dock((portA, dockA), (portB, dockB));
            groups.ForceUpdate();
            transforms.Unanchor(portB, em.GetComponent<TransformComponent>(portB));
            groups.ForceUpdate();
            CheckExamine(portB, false);
            Assert.That(nodesB["power"].NodeGroup, Is.Not.SameAs(wireB.NodeGroup));
            transforms.AnchorEntity(portB, em.GetComponent<TransformComponent>(portB));
            docking.Dock((portA, dockA), (portB, dockB));
            groups.ForceUpdate();
            em.DeleteEntity(portB);
            groups.ForceUpdate();
            Assert.That(dockA.Docked, Is.False);
            Solve();
            em.DeleteEntity(gridA.Owner);
            em.DeleteEntity(gridB.Owner);
        });
        await pair.CleanReturnAsync();
    }
    [TestCase("PowerlockLV", "PowerlockMV")]
    [TestCase("PowerlockLV", "PowerlockHV")]
    [TestCase("PowerlockMV", "PowerlockHV")]
    [TestCase("PowerlockLV", "Gaslock")]
    [TestCase("PowerlockMV", "Gaslock")]
    [TestCase("PowerlockHV", "Gaslock")]
    [TestCase("PowerlockLV", "AirlockShuttle")]
    [TestCase("PowerlockMV", "AirlockShuttle")]
    [TestCase("PowerlockHV", "AirlockShuttle")]
    public async Task RejectMismatchedPorts(string first, string second)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var em = server.ResolveDependency<IEntityManager>();
        var maps = server.ResolveDependency<IMapManager>();
        await server.WaitAssertion(() =>
        {
            var gridA = maps.CreateGridEntity(map.MapId);
            var gridB = maps.CreateGridEntity(map.MapId);
            var mapSystem = em.System<SharedMapSystem>();
            var transforms = em.System<SharedTransformSystem>();
            transforms.SetLocalPosition(gridB.Owner, new Vector2(0, -1));
            mapSystem.SetTile(gridA.Owner, gridA.Comp, Vector2i.Zero, new Tile(1));
            mapSystem.SetTile(gridB.Owner, gridB.Comp, Vector2i.Zero, new Tile(1));
            var a = em.SpawnEntity(first, new EntityCoordinates(gridA.Owner, new Vector2(0.5f, 0.5f)));
            var b = em.SpawnEntity(second, new EntityCoordinates(gridB.Owner, new Vector2(0.5f, 0.5f)));
            transforms.SetLocalRotation(b, Angle.FromDegrees(180));
            var docking = em.System<DockingSystem>();
            Assert.That(docking.CanDock((a, em.GetComponent<DockingComponent>(a)),
                (b, em.GetComponent<DockingComponent>(b))), Is.False);
            Assert.That(docking.GetDockingConfig(gridA.Owner, gridB.Owner), Is.Null);
            em.DeleteEntity(gridA.Owner);
            em.DeleteEntity(gridB.Owner);
        });
        await pair.CleanReturnAsync();
    }
}
