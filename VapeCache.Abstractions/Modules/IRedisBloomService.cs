namespace VapeCache.Abstractions.Modules;

/// <summary>
/// RedisBloom integration for probabilistic data structures.
/// </summary>
public interface IRedisBloomService
{
    /// <summary>
    /// Executes s available async.
    /// </summary>
    ValueTask<bool> IsAvailableAsync(CancellationToken ct = default);
    /// <summary>
    /// Executes add async.
    /// </summary>
    ValueTask<bool> AddAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct = default);
    /// <summary>
    /// Executes exists async.
    /// </summary>
    ValueTask<bool> ExistsAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct = default);
}
