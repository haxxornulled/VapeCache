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

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> PushFrontAsync(T item, CancellationToken ct = default)
    {
        var buffer = new ArrayBufferWriter<byte>();
        _codec.Serialize(buffer, item);
        return await _executor.LPushAsync(Key, buffer.WrittenMemory, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> PushBackAsync(T item, CancellationToken ct = default)
    {
        var buffer = new ArrayBufferWriter<byte>();
        _codec.Serialize(buffer, item);
        return await _executor.RPushAsync(Key, buffer.WrittenMemory, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<T?> PopFrontAsync(CancellationToken ct = default)
    {
        var bytes = await _executor.LPopAsync(Key, ct).ConfigureAwait(false);
        if (bytes is null) return default;
        return _codec.Deserialize(bytes);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<T?> PopBackAsync(CancellationToken ct = default)
    {
        var bytes = await _executor.RPopAsync(Key, ct).ConfigureAwait(false);
        if (bytes is null) return default;
        return _codec.Deserialize(bytes);
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryPopFrontAsync(CancellationToken ct, out ValueTask<T?> task)
    {
        if (!_executor.TryLPopAsync(Key, ct, out var bytesTask))
        {
            task = default;
            return false;
        }

        task = MapPopAsync(bytesTask, _codec);
        return true;
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryPopBackAsync(CancellationToken ct, out ValueTask<T?> task)
    {
        if (!_executor.TryRPopAsync(Key, ct, out var bytesTask))
        {
            task = default;
            return false;
        }

        task = MapPopAsync(bytesTask, _codec);
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
        if (pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

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

    private static ValueTask<T?> MapPopAsync(ValueTask<byte[]?> bytesTask, ICacheCodec<T> codec)
    {
        if (bytesTask.IsCompletedSuccessfully)
        {
            var bytes = bytesTask.Result;
            return new ValueTask<T?>(bytes is null ? default : codec.Deserialize(bytes));
        }

        return AwaitMapPopAsync(bytesTask, codec);

        static async ValueTask<T?> AwaitMapPopAsync(ValueTask<byte[]?> task, ICacheCodec<T> codec)
        {
            var bytes = await task.ConfigureAwait(false);
            if (bytes is null) return default;
            return codec.Deserialize(bytes);
        }
    }
}
