namespace Content.Server._NF.Power.Components;

/// <summary>Battery-backed power transfer between matching docked powerlocks.</summary>
[RegisterComponent]
public sealed partial class PowerlockComponent : Component
{
    // Runtime readings include local cable use and dock transfer, for the standard battery panel.
    [ViewVariables] public float Input;
    [ViewVariables] public float Output;
}
