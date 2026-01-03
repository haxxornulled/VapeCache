namespace VapeCache.Abstractions.Modules;

/// <summary>
/// RedisBloom integration for probabilistic data structures.
/// </summary>
public interface IRedisBloomService
{
    ValueTask<bool> IsAvailableAsync(CancellationToken ct = default);
    ValueTask<bool> AddAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct = default);
    ValueTask<bool> ExistsAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct = default);
}
