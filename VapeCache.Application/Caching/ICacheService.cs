using System.Buffers;

namespace VapeCache.Application.Caching;

public interface ICacheService
{
    string Name { get; }

    ValueTask<byte[]?> GetAsync(string key, CancellationToken ct);
    ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct);
    ValueTask<bool> RemoveAsync(string key, CancellationToken ct);

    ValueTask<T?> GetAsync<T>(string key, Func<ReadOnlySpan<byte>, T> deserialize, CancellationToken ct);
    ValueTask SetAsync<T>(string key, T value, Action<IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct);

    ValueTask<T> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        Action<IBufferWriter<byte>, T> serialize,
        Func<ReadOnlySpan<byte>, T> deserialize,
        CacheEntryOptions options,
        CancellationToken ct);
}
