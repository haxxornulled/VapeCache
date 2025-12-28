using System.Buffers;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Collections;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Collections;

/// <summary>
/// Typed Redis LIST implementation with zero-allocation serialization.
/// </summary>
internal sealed class CacheList<T> : ICacheList<T>
{
    private readonly IRedisCommandExecutor _executor;
    private readonly ICacheCodec<T> _codec;

    public string Key { get; }

    public CacheList(string key, IRedisCommandExecutor executor, ICacheCodec<T> codec)
    {
        Key = key;
        _executor = executor;
        _codec = codec;
    }

    public async ValueTask<long> PushFrontAsync(T item, CancellationToken ct = default)
    {
        var buffer = new ArrayBufferWriter<byte>();
        _codec.Serialize(buffer, item);
        return await _executor.LPushAsync(Key, buffer.WrittenMemory, ct).ConfigureAwait(false);
    }

    public async ValueTask<long> PushBackAsync(T item, CancellationToken ct = default)
    {
        var buffer = new ArrayBufferWriter<byte>();
        _codec.Serialize(buffer, item);
        return await _executor.RPushAsync(Key, buffer.WrittenMemory, ct).ConfigureAwait(false);
    }

    public async ValueTask<T?> PopFrontAsync(CancellationToken ct = default)
    {
        var bytes = await _executor.LPopAsync(Key, ct).ConfigureAwait(false);
        if (bytes is null) return default;
        return _codec.Deserialize(bytes);
    }

    public async ValueTask<T?> PopBackAsync(CancellationToken ct = default)
    {
        var bytes = await _executor.RPopAsync(Key, ct).ConfigureAwait(false);
        if (bytes is null) return default;
        return _codec.Deserialize(bytes);
    }

    public async ValueTask<T[]> RangeAsync(long start, long stop, CancellationToken ct = default)
    {
        var items = await _executor.LRangeAsync(Key, start, stop, ct).ConfigureAwait(false);
        var result = new T[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i] is not null)
                result[i] = _codec.Deserialize(items[i]!);
        }
        return result;
    }

    public ValueTask<long> LengthAsync(CancellationToken ct = default)
    {
        return _executor.LLenAsync(Key, ct);
    }
}
