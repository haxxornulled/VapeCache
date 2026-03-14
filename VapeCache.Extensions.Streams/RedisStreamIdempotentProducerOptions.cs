namespace VapeCache.Extensions.Streams;

/// <summary>
/// Options for idempotent Redis stream producer behavior.
/// </summary>
public sealed class RedisStreamIdempotentProducerOptions
{
    /// <summary>
    /// Default stream entry id value. Keep as "*" for auto-generated monotonic ids.
    /// </summary>
    public string DefaultEntryId { get; set; } = "*";

    /// <summary>
    /// When true, producer uses IDMPAUTO and computes idempotent identity from message content.
    /// </summary>
    public bool UseAutoIdempotentId { get; set; }
}
