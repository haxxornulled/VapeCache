using System;
using System.Collections.Generic;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;

namespace ResultDemo.Examples;

internal sealed class StubRedisCommandExecutor : IRedisCommandExecutor
{
    private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);
    private int _disposed;

    public IRedisBatch CreateBatch() => new NoopRedisBatch();

    public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_store.TryGetValue(key, out var v) ? v : null);
    }

    public bool TryGetAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        task = GetAsync(key, ct);
        return true;
    }

    public ValueTask<byte[]?> GetExAsync(string key, TimeSpan? ttl, CancellationToken ct) => GetAsync(key, ct);

    public bool TryGetExAsync(string key, TimeSpan? ttl, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        task = GetExAsync(key, ttl, ct);
        return true;
    }

    public ValueTask<byte[]?[]> MGetAsync(string[] keys, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var arr = new byte[]?[keys.Length];
        for (int i = 0; i < keys.Length; i++)
            _store.TryGetValue(keys[i], out arr[i]);
        return ValueTask.FromResult(arr);
    }

    public ValueTask<bool> SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _store[key] = value.ToArray();
        return ValueTask.FromResult(true);
    }

    public bool TrySetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct, out ValueTask<bool> task)
    {
        task = SetAsync(key, value, ttl, ct);
        return true;
    }

    public ValueTask<bool> MSetAsync((string Key, ReadOnlyMemory<byte> Value)[] items, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var (k, v) in items)
            _store[k] = v.ToArray();
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> DeleteAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_store.Remove(key));
    }

    public ValueTask<long> TtlSecondsAsync(string key, CancellationToken ct) => ValueTask.FromResult(-1L);
    public ValueTask<long> PTtlMillisecondsAsync(string key, CancellationToken ct) => ValueTask.FromResult(-1L);
    public ValueTask<long> UnlinkAsync(string key, CancellationToken ct) => ValueTask.FromResult(_store.Remove(key) ? 1L : 0L);
    public ValueTask<bool> ExpireAsync(string key, TimeSpan ttl, CancellationToken ct) => ValueTask.FromResult(true);

    public ValueTask<RedisValueLease> GetLeaseAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_store.TryGetValue(key, out var buffer))
            return ValueTask.FromResult(new RedisValueLease(buffer, buffer.Length, pooled: false));
        return ValueTask.FromResult(RedisValueLease.Null);
    }

    public bool TryGetLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        task = GetLeaseAsync(key, ct);
        return true;
    }

    public ValueTask<RedisValueLease> GetExLeaseAsync(string key, TimeSpan? ttl, CancellationToken ct) => GetLeaseAsync(key, ct);

    public bool TryGetExLeaseAsync(string key, TimeSpan? ttl, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        task = GetExLeaseAsync(key, ttl, ct);
        return true;
    }

    public ValueTask<long> HSetAsync(string key, string field, ReadOnlyMemory<byte> value, CancellationToken ct) => ValueTask.FromResult(1L);
    public ValueTask<byte[]?> HGetAsync(string key, string field, CancellationToken ct) => ValueTask.FromResult<byte[]?>(null);
    public bool TryHGetAsync(string key, string field, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        task = HGetAsync(key, field, ct);
        return true;
    }
    public ValueTask<byte[]?[]> HMGetAsync(string key, string[] fields, CancellationToken ct) => ValueTask.FromResult(Array.Empty<byte[]?>());
    public ValueTask<RedisValueLease> HGetLeaseAsync(string key, string field, CancellationToken ct) => ValueTask.FromResult(RedisValueLease.Null);

    public ValueTask<long> LPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct) => ValueTask.FromResult(1L);
    public ValueTask<long> RPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct) => ValueTask.FromResult(1L);
    public ValueTask<byte[]?> LPopAsync(string key, CancellationToken ct) => ValueTask.FromResult<byte[]?>(null);
    public bool TryLPopAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        task = LPopAsync(key, ct);
        return true;
    }
    public ValueTask<byte[]?> RPopAsync(string key, CancellationToken ct) => ValueTask.FromResult<byte[]?>(null);
    public bool TryRPopAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        task = RPopAsync(key, ct);
        return true;
    }
    public ValueTask<byte[]?[]> LRangeAsync(string key, long start, long stop, CancellationToken ct) => ValueTask.FromResult(Array.Empty<byte[]?>());
    public ValueTask<RedisListLease> LRangeLeaseAsync(string key, long start, long stop, CancellationToken ct) => ValueTask.FromResult(default(RedisListLease));
    public ValueTask<long> LLenAsync(string key, CancellationToken ct) => ValueTask.FromResult(0L);
    public ValueTask<RedisValueLease> LPopLeaseAsync(string key, CancellationToken ct) => ValueTask.FromResult(RedisValueLease.Null);
    public bool TryLPopLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        task = LPopLeaseAsync(key, ct);
        return true;
    }
    public ValueTask<RedisValueLease> RPopLeaseAsync(string key, CancellationToken ct) => ValueTask.FromResult(RedisValueLease.Null);
    public bool TryRPopLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        task = RPopLeaseAsync(key, ct);
        return true;
    }
    public ValueTask<byte[]?> LIndexAsync(string key, long index, CancellationToken ct) => ValueTask.FromResult<byte[]?>(null);
    public ValueTask<RedisValueLease> LIndexLeaseAsync(string key, long index, CancellationToken ct) => ValueTask.FromResult(RedisValueLease.Null);
    public ValueTask<byte[]?> GetRangeAsync(string key, long start, long end, CancellationToken ct) => ValueTask.FromResult<byte[]?>(null);

    public ValueTask<byte[]?> JsonGetAsync(string key, string? path, CancellationToken ct) => GetAsync(key, ct);
    public ValueTask<RedisValueLease> JsonGetLeaseAsync(string key, string? path, CancellationToken ct) => GetLeaseAsync(key, ct);
    public bool TryJsonGetLeaseAsync(string key, string? path, CancellationToken ct, out ValueTask<RedisValueLease> task) => TryGetLeaseAsync(key, ct, out task);
    public ValueTask<bool> JsonSetAsync(string key, string? path, ReadOnlyMemory<byte> json, CancellationToken ct) => SetAsync(key, json, null, ct);
    public ValueTask<bool> JsonSetLeaseAsync(string key, string? path, RedisValueLease json, CancellationToken ct) => SetAsync(key, json.Memory, null, ct);
    public async ValueTask<long> JsonDelAsync(string key, string? path, CancellationToken ct) => (await DeleteAsync(key, ct).ConfigureAwait(false)) ? 1L : 0L;

    public ValueTask<bool> FtCreateAsync(string index, string prefix, string[] fields, CancellationToken ct) => ValueTask.FromResult(true);
    public ValueTask<string[]> FtSearchAsync(string index, string query, int? offset, int? count, CancellationToken ct) => ValueTask.FromResult(Array.Empty<string>());
    public ValueTask<bool> BfAddAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct) => ValueTask.FromResult(true);
    public ValueTask<bool> BfExistsAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct) => ValueTask.FromResult(false);
    public ValueTask<bool> TsCreateAsync(string key, CancellationToken ct) => ValueTask.FromResult(true);
    public ValueTask<long> TsAddAsync(string key, long timestamp, double value, CancellationToken ct) => ValueTask.FromResult(timestamp);
    public ValueTask<(long Timestamp, double Value)[]> TsRangeAsync(string key, long from, long to, CancellationToken ct)
        => ValueTask.FromResult(Array.Empty<(long Timestamp, double Value)>());

    public ValueTask<long> SAddAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct) => ValueTask.FromResult(1L);
    public ValueTask<long> SRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct) => ValueTask.FromResult(1L);
    public ValueTask<bool> SIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct) => ValueTask.FromResult(false);
    public bool TrySIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct, out ValueTask<bool> task)
    {
        task = SIsMemberAsync(key, member, ct);
        return true;
    }
    public ValueTask<byte[]?[]> SMembersAsync(string key, CancellationToken ct) => ValueTask.FromResult(Array.Empty<byte[]?>());
    public ValueTask<long> SCardAsync(string key, CancellationToken ct) => ValueTask.FromResult(0L);

    public ValueTask<long> ZAddAsync(string key, double score, ReadOnlyMemory<byte> member, CancellationToken ct) => ValueTask.FromResult(0L);
    public ValueTask<long> ZRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct) => ValueTask.FromResult(0L);
    public ValueTask<long> ZCardAsync(string key, CancellationToken ct) => ValueTask.FromResult(0L);
    public ValueTask<double?> ZScoreAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct) => ValueTask.FromResult<double?>(null);
    public ValueTask<long?> ZRankAsync(string key, ReadOnlyMemory<byte> member, bool descending, CancellationToken ct) => ValueTask.FromResult<long?>(null);
    public ValueTask<double> ZIncrByAsync(string key, double increment, ReadOnlyMemory<byte> member, CancellationToken ct) => ValueTask.FromResult(0d);
    public ValueTask<(byte[] Member, double Score)[]> ZRangeWithScoresAsync(string key, long start, long stop, bool descending, CancellationToken ct)
        => ValueTask.FromResult(Array.Empty<(byte[] Member, double Score)>());
    public ValueTask<(byte[] Member, double Score)[]> ZRangeByScoreWithScoresAsync(
        string key,
        double min,
        double max,
        bool descending,
        long? offset,
        long? count,
        CancellationToken ct)
        => ValueTask.FromResult(Array.Empty<(byte[] Member, double Score)>());

    public async IAsyncEnumerable<string> ScanAsync(string? pattern = null, int pageSize = 128, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public async IAsyncEnumerable<byte[]> SScanAsync(string key, string? pattern = null, int pageSize = 128, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public async IAsyncEnumerable<(string Field, byte[] Value)> HScanAsync(string key, string? pattern = null, int pageSize = 128, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public async IAsyncEnumerable<(byte[] Member, double Score)> ZScanAsync(string key, string? pattern = null, int pageSize = 128, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask<string> PingAsync(CancellationToken ct) => ValueTask.FromResult("PONG");
    public ValueTask<string[]> ModuleListAsync(CancellationToken ct) => ValueTask.FromResult(Array.Empty<string>());

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _store.Clear();
        return ValueTask.CompletedTask;
    }
}

internal sealed class NoopRedisBatch : IRedisBatch
{
    public ValueTask QueueAsync(Func<IRedisCommandExecutor, CancellationToken, ValueTask> operation, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    public ValueTask<T> QueueAsync<T>(Func<IRedisCommandExecutor, CancellationToken, ValueTask<T>> operation, CancellationToken ct = default)
        => ValueTask.FromResult(default(T)!);

    public ValueTask ExecuteAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class NoopReconciliationService : IRedisReconciliationService
{
    public int PendingOperations => 0;
    public void TrackWrite(string key, ReadOnlyMemory<byte> value, TimeSpan? expiry) { }
    public void TrackDelete(string key) { }
    public ValueTask ReconcileAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public void Clear() { }
    public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
}
