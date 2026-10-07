// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared.Construction.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.Shared._NF.Construction;

/// <summary>Checks the complete rotated footprint before machine placement.</summary>
public sealed class MachineFootprintSystem : EntitySystem
{
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MachineFootprintComponent, AnchorAttemptEvent>(OnAnchorAttempt);
    }

    private void OnAnchorAttempt(Entity<MachineFootprintComponent> entity, ref AnchorAttemptEvent arguments)
    {
        var transform = Transform(entity);
        if (!IsFootprintClear(entity.Comp, transform.Coordinates, transform.LocalRotation, entity.Owner))
            arguments.Cancel();
    }

    public bool CanPlace(EntProtoId prototypeId, EntityCoordinates coordinates, Angle rotation, EntityUid ignoredEntity)
    {
        if (!_prototypeManager.Index(prototypeId).TryGetComponent<MachineFootprintComponent>(out var footprint, Factory))
            return true;

        return IsFootprintClear(footprint, coordinates, rotation, ignoredEntity);
    }

    public bool IsFootprintClear(MachineFootprintComponent footprint, EntityCoordinates coordinates,
        Angle rotation, EntityUid ignoredEntity)
    {
        var transform = EntityManager.System<SharedTransformSystem>();
        if (transform.GetGrid(coordinates) is not { } grid || !TryComp<MapGridComponent>(grid, out var gridComponent))
            return false;

        coordinates = _map.ToCenterCoordinates(grid, _map.TileIndicesFor(grid, gridComponent, coordinates));
        rotation = rotation.GetCardinalDir().ToAngle();

        foreach (var tileOffset in footprint.Tiles)
        {
            var tileCoordinates = new EntityCoordinates(grid,
                coordinates.Position + rotation.RotateVec(new Vector2(tileOffset.X, tileOffset.Y) * gridComponent.TileSize));
            if (_map.GetTileRef(grid, gridComponent, tileCoordinates).Tile.IsEmpty)
                return false;

            var tileBounds = new Box2Rotated(
                Box2.UnitCentered.Scale(0.98f * gridComponent.TileSize).Translated(transform.ToMapCoordinates(tileCoordinates).Position),
                transform.GetWorldRotation(grid), transform.ToMapCoordinates(tileCoordinates).Position);
            foreach (var intersectingEntity in _lookup.GetEntitiesIntersecting(grid, tileBounds, LookupFlags.Dynamic | LookupFlags.Static))
            {
                if (intersectingEntity != ignoredEntity)
                    return false;
            }
        }

        return true;
    }
}

