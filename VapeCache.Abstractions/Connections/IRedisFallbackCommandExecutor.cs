namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Defines a fallback Redis executor used when Redis is unavailable.
/// </summary>
public interface IRedisFallbackCommandExecutor : IRedisCommandExecutor
{
    /// <summary>
    /// Logical name for telemetry (e.g. "memory", "disk", "proxy").
    /// </summary>
    string Name { get; }

    // Extended operations used by the hybrid executor.
    ValueTask<bool> ExpireAsync(string key, TimeSpan ttl, CancellationToken ct);
    ValueTask<byte[]?> LIndexAsync(string key, long index, CancellationToken ct);
    ValueTask<byte[]?> GetRangeAsync(string key, long start, long end, CancellationToken ct);
}
