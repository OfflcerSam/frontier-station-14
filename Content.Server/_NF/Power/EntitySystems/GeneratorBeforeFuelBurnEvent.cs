// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._NF.Power.EntitySystems;

/// <summary>Checks fuel-burn prerequisites before fuel use and exhaust events.</summary>
[ByRefEvent]
public record struct GeneratorBeforeFuelBurnEvent(float FuelUsed)
{
    public bool Cancelled;
    public float PowerMultiplier = 1f;
}
