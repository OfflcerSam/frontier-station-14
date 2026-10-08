// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
using Content.Server._NF.Power.Components;
using Content.Server._NF.Power.Generator;
using Content.Server._NF.Power.Steam;
using Content.Server.Power.Generator;
using Content.Shared.Atmos;
using Content.Shared.Power.Generator;

namespace Content.Server._NF.Power.EntitySystems;

/// <summary>Shared observational prime-mover dynamics; actual turbine rotor safety stays in TurbineRotorSystem.</summary>
public sealed class GeneratorDriveSystem : EntitySystem
{
    [Dependency] private readonly GeneratorPipingSystem _piping = default!;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesAfter.Add(typeof(GeneratorSystem));
        UpdatesBefore.Add(typeof(GeneratorTelemetrySystem));
    }

    public override void Update(float frameTime)
    {
        if (!float.IsFinite(frameTime) || frameTime <= 0f)
            return;
        var query = EntityQueryEnumerator<StationaryGeneratorComponent, FuelGeneratorComponent>();
        while (query.MoveNext(out var uid, out _, out var fuel))
        {
            var drive = EnsureComp<GeneratorDriveComponent>(uid);
            if (!HasComp<TurbineRotorComponent>(uid))
            {
                var target = EngineTargetRpm(drive, fuel);
                var step = drive.MaximumOperatingRpm / Math.Max(drive.ResponseSeconds, 0.01f) * frameTime;
                drive.EngineRpm += Math.Clamp(target - drive.EngineRpm, -step, step);
            }
            var ambient = _piping.GetIntakeMixture(uid)?.Temperature ?? Atmospherics.T20C;
            var targetTemperature = ambient;
            if (fuel.On && TryComp<GeneratorTelemetryComponent>(uid, out var telemetry) && telemetry.BurnedThisFrame)
            {
                if (TryComp<SteamTurbineComponent>(uid, out var steam))
                    targetTemperature = Math.Max(steam.BoilingTemperature, steam.BoilerTemperature);
                else if (TryComp<PipedGasFuelComponent>(uid, out var gas))
                    targetTemperature = Math.Max(ambient, gas.ExhaustTemperature);
                else if (TryComp<GeneratorExhaustGasComponent>(uid, out var exhaust))
                    targetTemperature = Math.Max(ambient, exhaust.Temperature);
            }
            drive.WorkingTemperature = ApproachTemperature(drive.WorkingTemperature ?? ambient,
                targetTemperature, frameTime, drive.ThermalResponseSeconds);
        }
    }

    public static float EngineTargetRpm(GeneratorDriveComponent drive, FuelGeneratorComponent fuel) =>
        fuel.On ? drive.MaximumOperatingRpm * Math.Clamp(fuel.TargetPower / Math.Max(fuel.MaxTargetPower, 1f), 0f, 1f) : 0f;

    public static float ApproachTemperature(float current, float target, float seconds, float response) =>
        target + (current - target) * MathF.Exp(-Math.Max(seconds, 0f) / Math.Max(response, 0.01f));
}
