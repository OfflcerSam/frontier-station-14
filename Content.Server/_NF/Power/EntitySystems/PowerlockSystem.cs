using Content.Server._NF.Power.Components;
using Content.Server.NodeContainer.EntitySystems;
using Content.Server.NodeContainer.Nodes;
using Content.Server.Shuttles.Events;
using Content.Shared._NF.Power;
using Content.Shared.NodeContainer;

namespace Content.Server._NF.Power.EntitySystems;

public sealed class PowerlockSystem : EntitySystem
{
    [Dependency] private readonly NodeGroupSystem _nodeGroup = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PowerlockComponent, DockEvent>(OnDock);
        SubscribeLocalEvent<PowerlockComponent, UndockEvent>(OnUndock);
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
        // DockingSystem raises these events on both ports after updating DockedWith.
        // Reflood on undocking too: the port must retain its local cable connection.
        if (TryComp<NodeContainerComponent>(ent, out var container))
        {
            foreach (var node in container.Nodes.Values)
            {
                if (node is DockableCableNode)
                    _nodeGroup.QueueReflood(node);
            }
        }

        _appearance.SetData(ent, PowerlockVisuals.Docked, docked);
    }
}
