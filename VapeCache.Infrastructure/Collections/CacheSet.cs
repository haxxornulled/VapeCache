using System.Buffers;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Collections;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Collections;

/// <summary>
/// Typed Redis SET implementation with zero-allocation serialization.
/// </summary>
internal sealed class CacheSet<T> : ICacheSet<T>
{
    private readonly IRedisCommandExecutor _executor;
    private readonly ICacheCodec<T> _codec;

    public string Key { get; }

    public CacheSet(string key, IRedisCommandExecutor executor, ICacheCodec<T> codec)
    {
        Key = key;
        _executor = executor;
        _codec = codec;
    }

    /// <summary>
    /// Adds value.
    /// </summary>
    public async ValueTask<long> AddAsync(T item, CancellationToken ct = default)
    {
        var buffer = new ArrayBufferWriter<byte>();
        _codec.Serialize(buffer, item);
        return await _executor.SAddAsync(Key, buffer.WrittenMemory, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes value.
    /// </summary>
    public async ValueTask<long> RemoveAsync(T item, CancellationToken ct = default)
    {
        var buffer = new ArrayBufferWriter<byte>();
        _codec.Serialize(buffer, item);
        return await _executor.SRemAsync(Key, buffer.WrittenMemory, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> ContainsAsync(T item, CancellationToken ct = default)
    {
        var buffer = new ArrayBufferWriter<byte>();
        _codec.Serialize(buffer, item);
        return await _executor.SIsMemberAsync(Key, buffer.WrittenMemory, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<T[]> MembersAsync(CancellationToken ct = default)
    {
        var items = await _executor.SMembersAsync(Key, ct).ConfigureAwait(false);
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
    public ValueTask<long> CountAsync(CancellationToken ct = default)
    {
        return _executor.SCardAsync(Key, ct);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async IAsyncEnumerable<T> StreamAsync(
        string? pattern = null,
        int pageSize = 128,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var member in _executor.SScanAsync(Key, pattern, pageSize, ct).ConfigureAwait(false))
            yield return _codec.Deserialize(member);
    }
}
