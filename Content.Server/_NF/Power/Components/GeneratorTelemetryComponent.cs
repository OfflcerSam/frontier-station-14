// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Content.Server._NF.Power.Components;

/// <summary>Observed operating flows only; spills, refills and service transfers are excluded.</summary>
[RegisterComponent]
public sealed partial class GeneratorTelemetryComponent : Component
{
    public float FuelPending, OxygenPending, ExhaustPending, WaterPending;
    public float FuelRate, OxygenRate, ExhaustRate, WaterRate;
    public float RateElapsed;
    public bool BurnedThisFrame;
    public bool HasRateSample;
    public readonly GeneratorOutputHistory Output = new();
}

/// <summary>A time-weighted, trailing sixty-second history of delivered watts.</summary>
public sealed class GeneratorOutputHistory
{
    private readonly LinkedList<(double Seconds, double Watts)> _samples = new();
    private double _seconds;
    private double _joules;
    public float AverageWatts => _seconds > 0 ? (float) (_joules / _seconds) : 0f;

    public void Add(float seconds, float watts)
    {
        if (!float.IsFinite(seconds) || seconds <= 0f || !float.IsFinite(watts))
            return;
        var duration = Math.Min(seconds, 60f);
        watts = Math.Max(watts, 0f);
        _samples.AddLast((duration, watts));
        _seconds += duration;
        _joules += duration * (double) watts;
        while (_seconds > 60d && _samples.First is { } first)
        {
            var remove = Math.Min(_seconds - 60d, first.Value.Seconds);
            _seconds -= remove;
            _joules -= remove * first.Value.Watts;
            var remaining = first.Value.Seconds - remove;
            if (remaining <= 0d)
                _samples.RemoveFirst();
            else
                first.Value = (remaining, first.Value.Watts);
        }
    }
}
