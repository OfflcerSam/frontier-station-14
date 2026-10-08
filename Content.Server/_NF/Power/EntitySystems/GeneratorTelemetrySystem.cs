// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using Content.Server._NF.Power.Components;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Power.Generator;

namespace Content.Server._NF.Power.EntitySystems;

/// <summary>Observes completed transfers without changing generator or atmos balance.</summary>
public sealed class GeneratorTelemetrySystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        UpdatesAfter.Add(typeof(GeneratorSystem));
        UpdatesAfter.Add(typeof(PowerNetSystem));
    }

    public void Record(EntityUid uid, float fuel = 0f, float oxygen = 0f, float exhaust = 0f, float water = 0f)
    {
        if (!HasComp<StationaryGeneratorComponent>(uid))
            return;
        var data = EnsureComp<GeneratorTelemetryComponent>(uid);
        data.BurnedThisFrame |= fuel > 0f || oxygen > 0f || exhaust > 0f;
        data.FuelPending += fuel;
        data.OxygenPending += oxygen;
        data.ExhaustPending += exhaust;
        data.WaterPending += water;
    }

    public override void Update(float frameTime)
    {
        if (!float.IsFinite(frameTime) || frameTime <= 0f)
            return;
        var query = EntityQueryEnumerator<StationaryGeneratorComponent, PowerSupplierComponent>();
        while (query.MoveNext(out var uid, out _, out var supplier))
        {
            var data = EnsureComp<GeneratorTelemetryComponent>(uid);
            data.Output.Add(frameTime, supplier.Enabled && supplier.Net != null ? supplier.CurrentSupply : 0f);
            data.BurnedThisFrame = false;
            data.RateElapsed += frameTime;
            if (data.RateElapsed < 1f)
                continue;
            data.HasRateSample = true;
            data.FuelRate = data.FuelPending / data.RateElapsed;
            data.OxygenRate = data.OxygenPending / data.RateElapsed;
            data.ExhaustRate = data.ExhaustPending / data.RateElapsed;
            data.WaterRate = data.WaterPending / data.RateElapsed;
            data.FuelPending = data.OxygenPending = data.ExhaustPending = data.WaterPending = 0f;
            data.RateElapsed = 0f;
        }
    }
}
