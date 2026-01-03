using System.Buffers;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Collections;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Collections;

/// <summary>
/// Typed Redis ZSET implementation with zero-allocation serialization.
/// </summary>
internal sealed class CacheSortedSet<T> : ICacheSortedSet<T>
{
    private readonly IRedisCommandExecutor _executor;
    private readonly ICacheCodec<T> _codec;

    public string Key { get; }

    public CacheSortedSet(string key, IRedisCommandExecutor executor, ICacheCodec<T> codec)
    {
        Key = key;
        _executor = executor;
        _codec = codec;
    }

    public async ValueTask<long> AddAsync(T member, double score, CancellationToken ct = default)
    {
        var buffer = new ArrayBufferWriter<byte>();
        _codec.Serialize(buffer, member);
        return await _executor.ZAddAsync(Key, score, buffer.WrittenMemory, ct).ConfigureAwait(false);
    }

    public async ValueTask<long> RemoveAsync(T member, CancellationToken ct = default)
    {
        var buffer = new ArrayBufferWriter<byte>();
        _codec.Serialize(buffer, member);
        return await _executor.ZRemAsync(Key, buffer.WrittenMemory, ct).ConfigureAwait(false);
    }

    public async ValueTask<double?> ScoreAsync(T member, CancellationToken ct = default)
    {
        var buffer = new ArrayBufferWriter<byte>();
        _codec.Serialize(buffer, member);
        return await _executor.ZScoreAsync(Key, buffer.WrittenMemory, ct).ConfigureAwait(false);
    }

    public async ValueTask<long?> RankAsync(T member, bool descending = false, CancellationToken ct = default)
    {
        var buffer = new ArrayBufferWriter<byte>();
        _codec.Serialize(buffer, member);
        return await _executor.ZRankAsync(Key, buffer.WrittenMemory, descending, ct).ConfigureAwait(false);
    }

    public async ValueTask<double> IncrementAsync(T member, double increment, CancellationToken ct = default)
    {
        var buffer = new ArrayBufferWriter<byte>();
        _codec.Serialize(buffer, member);
        return await _executor.ZIncrByAsync(Key, increment, buffer.WrittenMemory, ct).ConfigureAwait(false);
    }

    public ValueTask<long> CountAsync(CancellationToken ct = default)
        => _executor.ZCardAsync(Key, ct);

    public async ValueTask<(T Member, double Score)[]> RangeByRankAsync(long start, long stop, bool descending = false, CancellationToken ct = default)
    {
        var items = await _executor.ZRangeWithScoresAsync(Key, start, stop, descending, ct).ConfigureAwait(false);
        var result = new (T Member, double Score)[items.Length];
        for (var i = 0; i < items.Length; i++)
            result[i] = (_codec.Deserialize(items[i].Member), items[i].Score);
        return result;
    }

    public async ValueTask<(T Member, double Score)[]> RangeByScoreAsync(
        double min,
        double max,
        bool descending = false,
        long? offset = null,
        long? count = null,
        CancellationToken ct = default)
    {
        var items = await _executor.ZRangeByScoreWithScoresAsync(Key, min, max, descending, offset, count, ct).ConfigureAwait(false);
        var result = new (T Member, double Score)[items.Length];
        for (var i = 0; i < items.Length; i++)
            result[i] = (_codec.Deserialize(items[i].Member), items[i].Score);
        return result;
    }

    public async IAsyncEnumerable<(T Member, double Score)> StreamAsync(
        string? pattern = null,
        int pageSize = 128,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var (member, score) in _executor.ZScanAsync(Key, pattern, pageSize, ct).ConfigureAwait(false))
            yield return (_codec.Deserialize(member), score);
    }
}
