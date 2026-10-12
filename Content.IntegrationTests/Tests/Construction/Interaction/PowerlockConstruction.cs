using System.Linq;
using Content.IntegrationTests.Tests.Interaction;
using Content.Shared._NF.Power;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.Construction.Interaction;

public sealed class PowerlockConstruction : InteractionTest
{
    [TestCase("PowerlockLVFrame")]
    [TestCase("PowerlockMVFrame")]
    [TestCase("PowerlockHVFrame")]
    public async Task ConstructPortablePowerlock(string prototype)
    {
        await StartConstruction("MachineFrame");
        await InteractUsing(Steel, 5);
        await Interact(Wrench, Cable);
        await InteractUsing(prototype + "MachineCircuitboard");
        await InteractUsing(Steel, 10);
        await InteractUsing(Battery1);
        if (prototype.Contains("LV"))
            await InteractUsing(Cable, 5);
        else
        {
            await InteractUsing(Cap1);
            await InteractUsing(prototype.Contains("MV") ? "CableMV" : "CableHV", 10);
        }
        await InteractUsing(Screw);
        AssertPrototype(prototype);
        await CheckCableSprite(prototype);
        await InteractUsing(Wrench);
        await Server.WaitAssertion(() => Assert.That(SEntMan.GetComponent<TransformComponent>(STarget!.Value).Anchored, Is.False));
    }

    [TestCase("PowerlockLVFrame")]
    [TestCase("PowerlockMVFrame")]
    [TestCase("PowerlockHVFrame")]
    public async Task DeconstructPortablePowerlock(string prototype)
    {
        await StartDeconstruction(prototype);
        await InteractUsing(Wrench);
        await Interact(Screw, Pry);
        AssertPrototype("MachineFrame");
        await Interact(Pry, Cut, Wrench, Screw);
        AssertDeleted();
        if (prototype.Contains("LV"))
            await AssertEntityLookup((Steel, 15), (Cable, 6), (Battery1, 1), (prototype + "MachineCircuitboard", 1));
        else
            await AssertEntityLookup((Steel, 15), (Cable, 1), (Battery1, 1), (Cap1, 1),
                (prototype.Contains("MV") ? "CableMV" : "CableHV", 10), (prototype + "MachineCircuitboard", 1));
    }

    [TestCase("PowerlockLV", "CableApcStack1")]
    [TestCase("PowerlockMV", "CableMVStack1")]
    [TestCase("PowerlockHV", "CableHVStack1")]
    public async Task ConstructPowerlock(string prototype, string cable)
    {
        await StartConstruction(prototype);
        await SpawnEntity((cable, 5), SEntMan.GetCoordinates(PlayerCoords));
        await InteractUsing(Steel, 5);
        AssertPrototype(prototype);
        foreach (var docked in new[] { false, true, false })
        {
            await Server.WaitPost(() => SEntMan.System<SharedAppearanceSystem>().SetData(STarget!.Value, PowerlockVisuals.Docked, docked));
            await RunTicks(5);
            await Client.WaitAssertion(() =>
            {
                var sprite = CEntMan.GetComponent<SpriteComponent>(CTarget!.Value);
                Assert.That(sprite["docked-unlit"].Visible, Is.EqualTo(docked));
            });
            await CheckCableSprite(prototype);
        }
    }

    private async Task CheckCableSprite(string prototype)
    {
        var voltage = prototype.Contains("LV") ? "lv" : prototype.Contains("MV") ? "mv" : "hv";
        await RunTicks(5);
        await Client.WaitAssertion(() =>
        {
            var sprite = CEntMan.GetComponent<SpriteComponent>(CTarget!.Value);
            Assert.That(sprite["cable"].RsiState, Is.EqualTo(new RSI.StateId(voltage + "Straight")));
            Assert.That(sprite["cable"].Color, Is.EqualTo(voltage == "lv" ? Robust.Shared.Maths.Color.Green :
                voltage == "mv" ? Robust.Shared.Maths.Color.Yellow : Robust.Shared.Maths.Color.White));
            Assert.That(sprite[0].Color, Is.EqualTo(Robust.Shared.Maths.Color.White), "The housing must not inherit cable tint.");
            Assert.That(sprite["cable-head"].RsiState, Is.EqualTo(new RSI.StateId(voltage + "HeadStraight")));
            Assert.That(sprite["cable-head"].Color, Is.EqualTo(Robust.Shared.Maths.Color.White), "Terminals must remain untinted.");
            Assert.That(sprite.AllLayers.Last(), Is.SameAs(sprite["cable-head"]), "Terminals must render above every other layer.");
            if (voltage == "mv")
            {
                Assert.That(sprite["cable-stripes"].RsiState, Is.EqualTo(new RSI.StateId("mvstripeStraight")));
                Assert.That(sprite["cable-stripes"].Color, Is.EqualTo(Robust.Shared.Maths.Color.White));
            }
        });
    }

    [TestCase("PowerlockLV", "CableApcStack1")]
    [TestCase("PowerlockMV", "CableMVStack1")]
    [TestCase("PowerlockHV", "CableHVStack1")]
    public async Task DeconstructPowerlock(string prototype, string cable)
    {
        await StartDeconstruction(prototype);
        await InteractUsing(Weld);
        AssertDeleted();
        await AssertEntityLookup((Steel, 5), (cable, 5));
    }
}
