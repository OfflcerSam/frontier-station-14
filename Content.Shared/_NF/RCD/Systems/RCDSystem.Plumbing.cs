using Content.Shared.Atmos.Components;
using Content.Shared.Tag;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Shared.RCD.Systems;

public sealed partial class RCDSystem
{
    private bool HasMatchingStructureOnTile(
        RCDPrototype recipe,
        ProtoId<TagPrototype> tag,
        EntityUid grid,
        MapGridComponent gridComponent,
        Vector2i position)
    {
        AtmosPipeLayer? layer = null;
        if (recipe.Prototype != null &&
            _protoManager.TryIndex<EntityPrototype>(recipe.Prototype, out var prototype) &&
            prototype.TryGetComponent<AtmosPipeLayersComponent>(out var layers, EntityManager.ComponentFactory))
        {
            // A manifold passes through all layers and needs the whole duct tile.
            if (!layers.PipeLayersLocked)
                layer = layers.CurrentPipeLayer;
        }

        var anchored = _mapSystem.GetAnchoredEntitiesEnumerator(grid, gridComponent, position);
        while (anchored.MoveNext(out var current))
        {
            if (current is not { } entity || !_tags.HasTag(entity, tag))
                continue;

            if (layer == null || !TryComp<AtmosPipeLayersComponent>(entity, out var existing) || existing.PipeLayersLocked || existing.CurrentPipeLayer == layer)
                return true;
        }

        return false;
    }
}
