// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Serialization;

namespace Content.Shared._NF.Construction;

[RegisterComponent]
public sealed partial class MachineFrameStatusVisualsComponent : Component
{
    [DataField]
    public string InitialState = "unwired";
}

[Serializable, NetSerializable]
public enum MachineFrameStatusVisuals : byte
{
    Stage,
}

[Serializable, NetSerializable]
public enum MachineFrameStatusLayers : byte
{
    Body,
}
