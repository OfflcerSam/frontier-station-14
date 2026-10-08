// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server._NF.Power.Isotope;
using Content.Server.Power.Components;
using Content.Server.Power.Generator;
using Content.Shared._NF.Power;
using Content.Shared.Damage;
using Content.Shared.Power.Generator;
using Content.Shared.Wires;

namespace Content.Server._NF.Power.EntitySystems;

/// <summary>Publishes independent operating, service-panel and casing-damage indicators.</summary>
public sealed class GeneratorStatusVisualsSystem : EntitySystem
{
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesAfter.Add(typeof(GeneratorSystem));
        UpdatesAfter.Add(typeof(IsotopeGeneratorSystem));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var query = EntityQueryEnumerator<GeneratorStatusVisualsComponent, AppearanceComponent>();
        while (query.MoveNext(out var uid, out _, out var appearance))
        {
            var running = TryComp<FuelGeneratorComponent>(uid, out var fuel)
                ? fuel.On
                : TryComp<PowerSupplierComponent>(uid, out var supplier) && supplier.Enabled;
            _appearance.SetData(uid, GeneratorVisuals.Running, running, appearance);
            _appearance.SetData(uid, GeneratorStatusVisuals.PanelOpen,
                TryComp<WiresPanelComponent>(uid, out var panel) && panel.Open, appearance);
            _appearance.SetData(uid, GeneratorStatusVisuals.Damaged,
                TryComp<DamageableComponent>(uid, out var damage) && damage.TotalDamage > 0, appearance);
        }
    }
}
