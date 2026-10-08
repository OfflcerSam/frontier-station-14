// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Robust.Shared.Maths;

namespace Content.Server._NF.Explosion;

/// <summary>Optional per-explosion limits, snapshotted before the source machine is destroyed.</summary>
public sealed class ExplosionAreaRestriction
{
    public float DamageRadius;
    public float FlashRadius;
    public EntityUid? BreachGrid;
    public HashSet<Vector2i> BreachTiles = new();

    public bool ContainsDamage(Vector2 center, Vector2 position) =>
        Vector2.DistanceSquared(center, position) <= DamageRadius * DamageRadius;

    public bool AllowsBreach(EntityUid grid, Vector2i tile) =>
        BreachGrid == grid && BreachTiles.Contains(tile);
}
