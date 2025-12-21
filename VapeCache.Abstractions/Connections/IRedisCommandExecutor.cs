namespace VapeCache.Abstractions.Connections;

public interface IRedisCommandExecutor : IAsyncDisposable
{
    ValueTask<byte[]?> GetAsync(string key, CancellationToken ct);
    ValueTask<byte[]?> GetExAsync(string key, TimeSpan? ttl, CancellationToken ct);
    ValueTask<byte[]?[]> MGetAsync(string[] keys, CancellationToken ct);

    ValueTask<bool> SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct);
    ValueTask<bool> MSetAsync((string Key, ReadOnlyMemory<byte> Value)[] items, CancellationToken ct);

    ValueTask<bool> DeleteAsync(string key, CancellationToken ct);
    ValueTask<long> TtlSecondsAsync(string key, CancellationToken ct);
    ValueTask<long> PTtlMillisecondsAsync(string key, CancellationToken ct);
    ValueTask<long> UnlinkAsync(string key, CancellationToken ct);
}
