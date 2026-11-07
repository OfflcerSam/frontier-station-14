using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype;

namespace Content.Server.NPC.Queries;

/// <summary>
/// Utility queries that run regularly to update an NPC without re-doing their thinking logic.
/// </summary>
[DataDefinition]
public sealed partial class UtilityService
{
    /// <summary>
    /// Identifier for this service in the task definition. // Frontier: cooldowns are tracked per task entry.
    /// </summary>
    [DataField("id", required: true)]
    public string ID = string.Empty;

    /// <summary>
    /// Prototype of the utility query.
    /// </summary>
    [DataField("proto", required: true, customTypeSerializer:typeof(PrototypeIdSerializer<UtilityQueryPrototype>))]
    public string Prototype = string.Empty;

    // Frontier: each running task samples its own interval for each service.
    [DataField("minCooldown")]
    public float MinCooldown = 0.25f;

    [DataField("maxCooldown")]
    public float MaxCooldown = 0.60f;
    // End Frontier

    /// <summary>
    /// Key to update with the utility query.
    /// </summary>
    [DataField("key", required: true)]
    public string Key = string.Empty;
}
