using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Collections;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;

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

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> PushFrontAsync(T item, CancellationToken ct = default)
    {
        using var buffer = new PooledByteBufferWriter();
        _codec.Serialize(buffer, item);
        return await _executor.LPushAsync(Key, buffer.WrittenMemory, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> PushBackAsync(T item, CancellationToken ct = default)
    {
        using var buffer = new PooledByteBufferWriter();
        _codec.Serialize(buffer, item);
        return await _executor.RPushAsync(Key, buffer.WrittenMemory, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<T?> PopFrontAsync(CancellationToken ct = default)
    {
        using var lease = await _executor.LPopLeaseAsync(Key, ct).ConfigureAwait(false);
        if (lease.IsNull) return default;
        return _codec.Deserialize(lease.Span);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<T?> PopBackAsync(CancellationToken ct = default)
    {
        using var lease = await _executor.RPopLeaseAsync(Key, ct).ConfigureAwait(false);
        if (lease.IsNull) return default;
        return _codec.Deserialize(lease.Span);
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryPopFrontAsync(CancellationToken ct, out ValueTask<T?> task)
    {
        if (!_executor.TryLPopLeaseAsync(Key, ct, out var leaseTask))
        {
            task = default;
            return false;
        }

        task = MapPopAsync(leaseTask, _codec);
        return true;
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryPopBackAsync(CancellationToken ct, out ValueTask<T?> task)
    {
        if (!_executor.TryRPopLeaseAsync(Key, ct, out var leaseTask))
        {
            task = default;
            return false;
        }

        task = MapPopAsync(leaseTask, _codec);
        return true;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
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

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<long> LengthAsync(CancellationToken ct = default)
    {
        return _executor.LLenAsync(Key, ct);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async IAsyncEnumerable<T> StreamAsync(
        int pageSize = 128,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        long start = 0;
        while (true)
        {
            var stop = start + pageSize - 1;
            var items = await _executor.LRangeAsync(Key, start, stop, ct).ConfigureAwait(false);
            if (items.Length == 0)
                yield break;

            foreach (var item in items)
            {
                if (item is not null)
                    yield return _codec.Deserialize(item);
            }

            if (items.Length < pageSize)
                yield break;

            start += items.Length;
        }
    }

    private static ValueTask<T?> MapPopAsync(ValueTask<RedisValueLease> leaseTask, ICacheCodec<T> codec)
    {
        if (leaseTask.IsCompletedSuccessfully)
        {
            using var lease = leaseTask.Result;
            return new ValueTask<T?>(lease.IsNull ? default : codec.Deserialize(lease.Span));
        }

        return AwaitMapPopAsync(leaseTask, codec);

        static async ValueTask<T?> AwaitMapPopAsync(ValueTask<RedisValueLease> task, ICacheCodec<T> codec)
        {
            using var lease = await task.ConfigureAwait(false);
            if (lease.IsNull) return default;
            return codec.Deserialize(lease.Span);
        }
    }
}
