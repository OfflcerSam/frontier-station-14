// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later
#nullable enable
using System.Linq;
using System.Numerics;
using Content.Client._NF.Power;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._NF.Power.EntitySystems;
using Content.Server._NF.Power.Generator;
using Content.Server.Power.Generator;
using Content.Shared._NF.Power;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Content.Shared.Ghost;
using Content.Shared.Hands.Components;
using Content.Shared.Power.Generator;
using Content.Shared.Verbs;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NF.Power;

public sealed class GeneratorControlsRevisionTests : InteractionTest
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task OnlyInteractingGhostCanUseHandlessControls(bool allowed)
    {
        await SpawnTarget("NFStationaryGeneratorGasTurbineCommercialEmpty");
        await Server.WaitAssertion(() =>
        {
            var uid = STarget!.Value;
            SEntMan.RemoveComponent<HandsComponent>(SPlayer);
            SEntMan.EnsureComponent<GhostComponent>(SPlayer);
            SEntMan.System<SharedGhostSystem>().SetCanGhostInteract(SPlayer, allowed);
            var verbs = new GetVerbsEvent<ActivationVerb>(SPlayer, uid, null, null, allowed, true, true, new());
            SEntMan.EventBus.RaiseLocalEvent(uid, verbs);
            Assert.That(verbs.Verbs.Any(v => v.Text == Robust.Shared.Localization.Loc.GetString("ui-verb-toggle-open")), Is.EqualTo(allowed));
            SEntMan.EventBus.RaiseLocalEvent(uid, new PortableGeneratorStartMessage { Actor = SPlayer });
            var active = SEntMan.GetComponent<DoAfterComponent>(SPlayer).DoAfters.Values.ToArray();
            Assert.That(active.Length, Is.EqualTo(allowed ? 1 : 0));
            if (allowed)
                Assert.That(active[0].Args.NeedHand, Is.False);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task GasMechanicalFragmentsDoNotDoubleBurst(bool killingHit)
    {
        await SpawnTarget("NFStationaryGeneratorGasTurbineCommercialEmpty");
        await Server.WaitAssertion(() =>
        {
            var uid = STarget!.Value;
            var before = SEntMan.EntityQuery<GeneratorShrapnelComponent>().Count();
            SEntMan.System<GeneratorSystem>().SetFuelGeneratorOn(uid, true);
            void Hit(int amount) => SEntMan.System<DamageableSystem>().TryChangeDamage(uid,
                new DamageSpecifier { DamageDict = new() { ["Blunt"] = FixedPoint2.New(amount) } }, true);
            if (killingHit)
            {
                Hit(200);
                Assert.That(SEntMan.EntityQuery<GeneratorShrapnelComponent>().Count() - before, Is.EqualTo(10));
            }
            else
            {
                Hit(50);
                Assert.That(SEntMan.EntityQuery<GeneratorShrapnelComponent>().Count() - before, Is.EqualTo(5));
                Hit(50);
                Assert.That(SEntMan.EntityQuery<GeneratorShrapnelComponent>().Count() - before, Is.EqualTo(5));
                Hit(100);
                Assert.That(SEntMan.EntityQuery<GeneratorShrapnelComponent>().Count() - before, Is.EqualTo(15));
            }
        });
    }

    [Test]
    public async Task DedicatedWindowKeepsSizeStableAcrossFuelDigits()
    {
        await SpawnTarget("NFStationaryGeneratorGasTurbineCommercialEmpty");
        await Client.WaitAssertion(() =>
        {
            using var window = new StationaryGeneratorWindow();
            window.SetEntity(CTarget!.Value);
            var fuel = CEntMan.GetComponent<FuelGeneratorComponent>(CTarget.Value);
            var state = new PortableGeneratorComponentBuiState(fuel, 1000, false, null)
            {
                PipedGasFuel = true,
                Stationary = new StationaryGeneratorUiData { Rpm = 2000, OxygenMoles = 100, ExhaustPressure = 100 }
            };
            window.Update(state);
            window.Measure(new Vector2(700, 600));
            var before = window.DesiredSize;
            state.RemainingFuel = 9.99f;
            window.Update(state);
            window.Measure(new Vector2(700, 600));
            Assert.That(window.DesiredSize, Is.EqualTo(before));
            Assert.That(window.Resizable, Is.True);
        });
    }

    [Test]
    public async Task DiagnosticsMatchRotorSafetyState()
    {
        await SpawnTarget("NFStationaryGeneratorGasTurbineCommercialEmpty");
        await Server.WaitAssertion(() =>
        {
            var uid = STarget!.Value;
            var rotor = SEntMan.GetComponent<TurbineRotorComponent>(uid);
            var system = SEntMan.System<StationaryGeneratorDiagnosticsSystem>();
            rotor.Rpm = 3400;
            Assert.That(system.GetData(uid)!.RotorState, Is.EqualTo("warning"));
            rotor.Rpm = 3700;
            Assert.That(system.GetData(uid)!.SafetyState, Is.EqualTo("governor"));
            rotor.Tripped = true;
            Assert.That(system.GetData(uid)!.SafetyState, Is.EqualTo("tripped"));
        });
    }
}
