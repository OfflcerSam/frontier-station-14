// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._NF.Power.Components;

/// <summary>
/// Rated values for a stationary fuel generator. Upgrades are calculated from
/// these values each time parts change, so repeated refreshes never compound.
/// </summary>
[RegisterComponent]
public sealed partial class StationaryGeneratorComponent : Component
{
    [DataField(required: true)]
    public float RatedPower;

    [DataField(required: true)]
    public float RatedBurnRate;

    [DataField(required: true)]
    public float RatedRampRate;

    [DataField(required: true)]
    public float RatedFuelCapacity;

    [DataField]
    public string FuelSolution = "tank";

    /// <summary>Zero disables room-oxygen consumption for other generator technologies.</summary>
    [DataField]
    public float OxygenMolesPerFuelUnit;

    [DataField]
    public float CapacitorRating = 1f;

    [DataField]
    public float ManipulatorRating = 1f;

    [DataField]
    public float MatterBinRating = 1f;
}

