// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._NF.Power.EntitySystems;

/// <summary>Checks prerequisites before a start attempt reports success or failure.</summary>
[ByRefEvent]
public record struct GeneratorStartAttemptEvent
{
    public string? FailureMessage;
}
