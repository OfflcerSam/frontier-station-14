using Robust.Shared.Audio;

namespace Content.Shared._Starlight.Medical.Items.Components;

[RegisterComponent, Robust.Shared.GameStates.NetworkedComponent]
public sealed partial class PatchComponent : Component
{
    public const string SolutionName = "patch";

    [DataField]
    public string SolutionContainer = SolutionName;

    [DataField]
    public float Delay = 3f;

    [DataField("healingBeginSound")]
    public SoundSpecifier? ApplyBeginSound;

    [DataField("healingEndSound")]
    public SoundSpecifier? ApplyEndSound;
}
