using Robust.Shared.Map;

namespace Content.Server.NPC.Components;

internal readonly record struct NPCThreatStimulus(
    EntityUid Source, EntityCoordinates Location, TimeSpan RecordedAt, TimeSpan ExpiresAt);

internal readonly record struct NPCThreatObservation(
    EntityUid Target, EntityCoordinates? Location, TimeSpan SampledAt);
