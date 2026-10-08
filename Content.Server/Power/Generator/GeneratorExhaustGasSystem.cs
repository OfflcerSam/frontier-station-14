using Content.Server.Atmos;
using Content.Server.Atmos.EntitySystems;
using Content.Shared.Atmos;
using Content.Shared.Power.Generator;

namespace Content.Server.Power.Generator;

/// <seealso cref="GeneratorSystem"/>
/// <seealso cref="GeneratorExhaustGasComponent"/>
public sealed class GeneratorExhaustGasSystem : EntitySystem
{
    // Frontier: route piped generators through the existing atmos network.
    [Dependency] private readonly Content.Server._NF.Power.EntitySystems.GeneratorPipingSystem _piping = default!;
    [Dependency] private readonly AtmosphereSystem _atmosphere = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<GeneratorExhaustGasComponent, GeneratorUseFuel>(FuelUsed);
    }

    private void FuelUsed(EntityUid uid, GeneratorExhaustGasComponent component, GeneratorUseFuel args)
    {
        var exhaustMixture = new GasMixture();
        exhaustMixture.SetMoles(component.GasType, args.FuelUsed * component.MoleRatio);
        exhaustMixture.Temperature = component.Temperature;

        // Frontier: share routine emissions among connected generator exhaust ports.
        _piping.ReleaseExhaust(uid, exhaustMixture);
    }
}
