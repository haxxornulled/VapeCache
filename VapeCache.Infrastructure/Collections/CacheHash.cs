using System.Buffers;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Collections;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Collections;

/// <summary>
/// Typed Redis HASH implementation with zero-allocation serialization.
/// </summary>
internal sealed class CacheHash<T> : ICacheHash<T>
{
    private readonly IRedisCommandExecutor _executor;
    private readonly ICacheCodec<T> _codec;

    public string Key { get; }

    public CacheHash(string key, IRedisCommandExecutor executor, ICacheCodec<T> codec)
    {
        Key = key;
        _executor = executor;
        _codec = codec;
    }

    public async ValueTask<long> SetAsync(string field, T value, CancellationToken ct = default)
    {
        var buffer = new ArrayBufferWriter<byte>();
        _codec.Serialize(buffer, value);
        return await _executor.HSetAsync(Key, field, buffer.WrittenMemory, ct).ConfigureAwait(false);
    }

    public async ValueTask<T?> GetAsync(string field, CancellationToken ct = default)
    {
        var bytes = await _executor.HGetAsync(Key, field, ct).ConfigureAwait(false);
        if (bytes is null) return default;
        return _codec.Deserialize(bytes);
    }

    public async ValueTask<T?[]> GetManyAsync(string[] fields, CancellationToken ct = default)
    {
        var items = await _executor.HMGetAsync(Key, fields, ct).ConfigureAwait(false);
        var result = new T?[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i] is not null)
                result[i] = _codec.Deserialize(items[i]!);
        }
        return result;
    }
}
