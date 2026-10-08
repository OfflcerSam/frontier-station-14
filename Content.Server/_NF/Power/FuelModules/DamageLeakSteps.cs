// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Content.Server._NF.Power.FuelModules;

/// <summary>Counts newly crossed damage marks, including the first mark and excluding repair.</summary>
public static class DamageLeakSteps
{
    public static float Count(float previous, float current, float start, float step)
    {
        if (step <= 0f || current <= previous)
            return 0f;
        static double Marks(float value, float start, float step) =>
            Math.Max(0, 1 + Math.Floor(Math.Round((Math.Min(value, 1f) - start) / step, 5)));
        return (float) Math.Max(0, Marks(current, start, step) - Marks(previous, start, step));
    }
}
