namespace VapeCache.Extensions.Streams;

/// <summary>
/// Produces idempotent Redis stream entries using Redis 8.6 IDMP/IDMPAUTO support.
/// </summary>
public interface IRedisStreamIdempotentProducer
{
    /// <summary>
    /// Publishes a stream entry and returns the entry id returned by Redis.
    /// </summary>
    ValueTask<string> PublishAsync(
        string key,
        string producerId,
        string? idempotentId,
        (string Field, ReadOnlyMemory<byte> Value)[] fields,
        CancellationToken ct = default);

    /// <summary>
    /// Configures per-stream idempotence retention.
    /// </summary>
    ValueTask<bool> ConfigureIdempotenceAsync(
        string key,
        int? durationSeconds,
        int? maxSize,
        CancellationToken ct = default);
}
