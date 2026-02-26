using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;
using VapeCache.Reconciliation;
using Xunit;

namespace VapeCache.Tests.Caching;

public sealed class RedisCircuitBreakerHybridCacheTests
{
    [Fact]
    public async Task Breaker_opens_after_threshold_and_stops_hammering_redis()
    {
        var time = new ManualTimeProvider();
        var redisExec = new ThrowingExecutor();
        await using var _ = redisExec.ConfigureAwait(false);

        var current = new CurrentCacheService();
        var statsRegistry = new CacheStatsRegistry();
        var redis = new RedisCacheService(redisExec, current, statsRegistry);
        var memory = CreateMemoryCacheService(current, statsRegistry);

        await memory.SetAsync("k", "v"u8.ToArray(), new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);

        var breaker = Options.Create(new RedisCircuitBreakerOptions
        {
            Enabled = true,
            ConsecutiveFailuresToOpen = 2,
            BreakDuration = TimeSpan.FromSeconds(10),
            HalfOpenProbeTimeout = TimeSpan.FromMilliseconds(1)
        });

        var hybrid = new HybridCacheService(redis, memory, current, time, breaker, statsRegistry, NullLogger<HybridCacheService>.Instance);

        // Two failures => open.
        Assert.Equal("v"u8.ToArray(), await hybrid.GetAsync("k", CancellationToken.None));
        Assert.Equal("v"u8.ToArray(), await hybrid.GetAsync("k", CancellationToken.None));
        Assert.Equal(2, redisExec.GetCalls);

        // Open: no more redis calls.
        Assert.Equal("v"u8.ToArray(), await hybrid.GetAsync("k", CancellationToken.None));
        Assert.Equal(2, redisExec.GetCalls);
        Assert.Equal("memory", current.CurrentName);

        // After break duration: allow one probe.
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal("v"u8.ToArray(), await hybrid.GetAsync("k", CancellationToken.None));
        Assert.Equal(3, redisExec.GetCalls);
    }

    [Fact]
    public async Task Forced_open_disables_redis_until_cleared()
    {
        var time = new ManualTimeProvider();
        var redisExec = new ThrowingExecutor();
        await using var _ = redisExec.ConfigureAwait(false);

        var current = new CurrentCacheService();
        var statsRegistry = new CacheStatsRegistry();
        var redis = new RedisCacheService(redisExec, current, statsRegistry);
        var memory = CreateMemoryCacheService(current, statsRegistry);
        await memory.SetAsync("k", "v"u8.ToArray(), new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);

        var breaker = Options.Create(new RedisCircuitBreakerOptions
        {
            Enabled = true,
            ConsecutiveFailuresToOpen = 2,
            BreakDuration = TimeSpan.FromSeconds(10),
            HalfOpenProbeTimeout = TimeSpan.FromMilliseconds(1)
        });

        var hybrid = new HybridCacheService(redis, memory, current, time, breaker, statsRegistry, NullLogger<HybridCacheService>.Instance);

        // Force open: no redis calls at all.
        ((IRedisFailoverController)hybrid).ForceOpen("test");
        Assert.Equal("v"u8.ToArray(), await hybrid.GetAsync("k", CancellationToken.None));
        Assert.Equal(0, redisExec.GetCalls);
        Assert.Equal("memory", current.CurrentName);

        // Clear: first call attempts redis (will fail and fall back).
        ((IRedisFailoverController)hybrid).ClearForcedOpen();
        Assert.Equal("v"u8.ToArray(), await hybrid.GetAsync("k", CancellationToken.None));
        Assert.Equal(1, redisExec.GetCalls);
    }

    [Fact]
    public async Task Reconciliation_runs_after_breaker_closes_and_replays_pending_writes()
    {
        var time = new ManualTimeProvider();
        var flakyRedis = new FlakyExecutor(failuresBeforeSuccess: 1);
        await using var _ = flakyRedis.ConfigureAwait(false);

        var current = new CurrentCacheService();
        var statsRegistry = new CacheStatsRegistry();
        var redis = new RedisCacheService(flakyRedis, current, statsRegistry);
        var memory = CreateMemoryCacheService(current, statsRegistry);

        var breaker = Options.Create(new RedisCircuitBreakerOptions
        {
            Enabled = true,
            ConsecutiveFailuresToOpen = 1,
            BreakDuration = TimeSpan.FromSeconds(1),
            HalfOpenProbeTimeout = TimeSpan.FromMilliseconds(50)
        });

        var reconciliationOptions = Options.Create(new RedisReconciliationOptions
        {
            Enabled = true,
            BatchSize = 100,
            MaxRunDuration = TimeSpan.FromSeconds(2)
        });
        var store = new InMemoryReconciliationStore();
        var reconciliation = new RedisReconciliationService(new TestReconciliationExecutor(flakyRedis), reconciliationOptions, NullLogger<RedisReconciliationService>.Instance, time, store);

        var hybrid = new HybridCacheService(redis, memory, current, time, breaker, statsRegistry, NullLogger<HybridCacheService>.Instance, reconciliation);

        // First call fails redis and opens the breaker.
        Assert.Null(await hybrid.GetAsync("demo", CancellationToken.None));

        // Write while open -> goes to memory and is tracked for reconciliation.
        var payload = "hello"u8.ToArray();
        await hybrid.SetAsync("demo", payload, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);
        Assert.Equal(1, reconciliation.PendingOperations);

        // Allow breaker to transition to half-open/closed and make redis succeed.
        time.Advance(TimeSpan.FromSeconds(2));

        var fetched = await hybrid.GetAsync("demo", CancellationToken.None);
        Assert.Equal(payload, fetched);

        // Wait for reconciliation to push the write to redis and clear pending ops.
        var sw = Stopwatch.StartNew();
        while (reconciliation.PendingOperations > 0 && sw.Elapsed < TimeSpan.FromSeconds(2))
        {
            await Task.Delay(25);
        }

        Assert.Equal(0, reconciliation.PendingOperations);
        Assert.True(flakyRedis.TryGetValue("demo", out var redisValue));
        Assert.Equal(payload, redisValue);
    }

    private sealed class ThrowingExecutor : IRedisCommandExecutor
    {
        public int GetCalls;

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
        {
            Interlocked.Increment(ref GetCalls);
            throw new InvalidOperationException("redis down");
        }

        public bool TryGetAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task) => throw new InvalidOperationException("redis down");
        public ValueTask<byte[]?> GetExAsync(string key, TimeSpan? ttl, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public bool TryGetExAsync(string key, TimeSpan? ttl, CancellationToken ct, out ValueTask<byte[]?> task) => throw new InvalidOperationException("redis down");
        public ValueTask<byte[]?[]> MGetAsync(string[] keys, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<bool> SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public bool TrySetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct, out ValueTask<bool> task) => throw new InvalidOperationException("redis down");
        public ValueTask<bool> MSetAsync((string Key, ReadOnlyMemory<byte> Value)[] items, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<bool> DeleteAsync(string key, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<long> TtlSecondsAsync(string key, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<long> PTtlMillisecondsAsync(string key, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<long> UnlinkAsync(string key, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<RedisValueLease> GetLeaseAsync(string key, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<RedisValueLease> GetExLeaseAsync(string key, TimeSpan? ttl, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public bool TryGetLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task) => throw new InvalidOperationException("redis down");
        public bool TryGetExLeaseAsync(string key, TimeSpan? ttl, CancellationToken ct, out ValueTask<RedisValueLease> task) => throw new InvalidOperationException("redis down");
        public ValueTask<long> HSetAsync(string key, string field, ReadOnlyMemory<byte> value, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<byte[]?> HGetAsync(string key, string field, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public bool TryHGetAsync(string key, string field, CancellationToken ct, out ValueTask<byte[]?> task) => throw new InvalidOperationException("redis down");
        public ValueTask<byte[]?[]> HMGetAsync(string key, string[] fields, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<RedisValueLease> HGetLeaseAsync(string key, string field, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<long> LPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<byte[]?> LPopAsync(string key, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public bool TryLPopAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task) => throw new InvalidOperationException("redis down");
        public ValueTask<byte[]?[]> LRangeAsync(string key, long start, long stop, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<RedisValueLease> LPopLeaseAsync(string key, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<RedisValueLease> RPopLeaseAsync(string key, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public bool TryLPopLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task) => throw new InvalidOperationException("redis down");
        public bool TryRPopLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task) => throw new InvalidOperationException("redis down");
        public ValueTask<long> RPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<long> RPushManyAsync(string key, ReadOnlyMemory<byte>[] values, int count, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<byte[]?> RPopAsync(string key, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public bool TryRPopAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task) => throw new InvalidOperationException("redis down");
        public ValueTask<long> LLenAsync(string key, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<long> SAddAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<long> SRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<bool> SIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public bool TrySIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct, out ValueTask<bool> task) => throw new InvalidOperationException("redis down");
        public ValueTask<byte[]?[]> SMembersAsync(string key, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<long> SCardAsync(string key, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<string> PingAsync(CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<string[]> ModuleListAsync(CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<bool> ExpireAsync(string key, TimeSpan ttl, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<byte[]?> LIndexAsync(string key, long index, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<byte[]?> GetRangeAsync(string key, long start, long end, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public IRedisBatch CreateBatch() => new NoopBatch();
        public ValueTask<byte[]?> JsonGetAsync(string key, string? path, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<RedisValueLease> JsonGetLeaseAsync(string key, string? path, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public bool TryJsonGetLeaseAsync(string key, string? path, CancellationToken ct, out ValueTask<RedisValueLease> task) => throw new InvalidOperationException("redis down");
        public ValueTask<bool> JsonSetAsync(string key, string? path, ReadOnlyMemory<byte> json, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<bool> JsonSetLeaseAsync(string key, string? path, RedisValueLease json, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<long> JsonDelAsync(string key, string? path, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<bool> FtCreateAsync(string index, string prefix, string[] fields, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<string[]> FtSearchAsync(string index, string query, int? offset, int? count, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<bool> BfAddAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<bool> BfExistsAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<bool> TsCreateAsync(string key, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<long> TsAddAsync(string key, long timestamp, double value, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<(long Timestamp, double Value)[]> TsRangeAsync(string key, long from, long to, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<long> ZAddAsync(string key, double score, ReadOnlyMemory<byte> member, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<long> ZRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<long> ZCardAsync(string key, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<double?> ZScoreAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<long?> ZRankAsync(string key, ReadOnlyMemory<byte> member, bool descending, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<double> ZIncrByAsync(string key, double increment, ReadOnlyMemory<byte> member, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<(byte[] Member, double Score)[]> ZRangeWithScoresAsync(string key, long start, long stop, bool descending, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public ValueTask<(byte[] Member, double Score)[]> ZRangeByScoreWithScoresAsync(string key, double min, double max, bool descending, long? offset, long? count, CancellationToken ct) => throw new InvalidOperationException("redis down");
        public IAsyncEnumerable<string> ScanAsync(string? pattern = null, int pageSize = 128, CancellationToken ct = default) => throw new InvalidOperationException("redis down");
        public IAsyncEnumerable<byte[]> SScanAsync(string key, string? pattern = null, int pageSize = 128, CancellationToken ct = default) => throw new InvalidOperationException("redis down");
        public IAsyncEnumerable<(string Field, byte[] Value)> HScanAsync(string key, string? pattern = null, int pageSize = 128, CancellationToken ct = default) => throw new InvalidOperationException("redis down");
        public IAsyncEnumerable<(byte[] Member, double Score)> ZScanAsync(string key, string? pattern = null, int pageSize = 128, CancellationToken ct = default) => throw new InvalidOperationException("redis down");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FlakyExecutor : IRedisCommandExecutor
    {
        private readonly ConcurrentDictionary<string, byte[]> _store = new(StringComparer.Ordinal);
        private int _failuresRemaining;

        public FlakyExecutor(int failuresBeforeSuccess) => _failuresRemaining = failuresBeforeSuccess;

        public void FailNext(int count) => Interlocked.Exchange(ref _failuresRemaining, count);

        public bool TryGetValue(string key, out byte[]? value) => _store.TryGetValue(key, out value);

        private void MaybeFail()
        {
            while (true)
            {
                var remaining = Volatile.Read(ref _failuresRemaining);
                if (remaining <= 0)
                    return;

                var after = Interlocked.Decrement(ref _failuresRemaining);
                if (after >= 0)
                    throw new InvalidOperationException("redis down");
            }
        }

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
        {
            MaybeFail();
            _store.TryGetValue(key, out var value);
            return ValueTask.FromResult<byte[]?>(value);
        }

        public bool TryGetAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task) => throw new NotSupportedException();
        public ValueTask<byte[]?> GetExAsync(string key, TimeSpan? ttl, CancellationToken ct) => GetAsync(key, ct);
        public bool TryGetExAsync(string key, TimeSpan? ttl, CancellationToken ct, out ValueTask<byte[]?> task) => throw new NotSupportedException();
        public ValueTask<byte[]?[]> MGetAsync(string[] keys, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<bool> SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct)
        {
            MaybeFail();
            _store[key] = value.ToArray();
            return ValueTask.FromResult(true);
        }

        public bool TrySetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct, out ValueTask<bool> task) => throw new NotSupportedException();
        public ValueTask<bool> MSetAsync((string Key, ReadOnlyMemory<byte> Value)[] items, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<bool> DeleteAsync(string key, CancellationToken ct)
        {
            MaybeFail();
            return ValueTask.FromResult(_store.TryRemove(key, out _));
        }

        public ValueTask<long> TtlSecondsAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<long> PTtlMillisecondsAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<long> UnlinkAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<RedisValueLease> GetLeaseAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<RedisValueLease> GetExLeaseAsync(string key, TimeSpan? ttl, CancellationToken ct) => throw new NotSupportedException();
        public bool TryGetLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task) => throw new NotSupportedException();
        public bool TryGetExLeaseAsync(string key, TimeSpan? ttl, CancellationToken ct, out ValueTask<RedisValueLease> task) => throw new NotSupportedException();
        public ValueTask<long> HSetAsync(string key, string field, ReadOnlyMemory<byte> value, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<byte[]?> HGetAsync(string key, string field, CancellationToken ct) => throw new NotSupportedException();
        public bool TryHGetAsync(string key, string field, CancellationToken ct, out ValueTask<byte[]?> task) => throw new NotSupportedException();
        public ValueTask<byte[]?[]> HMGetAsync(string key, string[] fields, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<RedisValueLease> HGetLeaseAsync(string key, string field, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<long> LPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<byte[]?> LPopAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public bool TryLPopAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task) => throw new NotSupportedException();
        public ValueTask<byte[]?[]> LRangeAsync(string key, long start, long stop, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<RedisValueLease> LPopLeaseAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<RedisValueLease> RPopLeaseAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public bool TryLPopLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task) => throw new NotSupportedException();
        public bool TryRPopLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task) => throw new NotSupportedException();
        public ValueTask<long> RPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<long> RPushManyAsync(string key, ReadOnlyMemory<byte>[] values, int count, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<byte[]?> RPopAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public bool TryRPopAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task) => throw new NotSupportedException();
        public ValueTask<long> LLenAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<long> SAddAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<long> SRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> SIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct) => throw new NotSupportedException();
        public bool TrySIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct, out ValueTask<bool> task) => throw new NotSupportedException();
        public ValueTask<byte[]?[]> SMembersAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<long> SCardAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<string> PingAsync(CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<string[]> ModuleListAsync(CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> ExpireAsync(string key, TimeSpan ttl, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<byte[]?> LIndexAsync(string key, long index, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<byte[]?> GetRangeAsync(string key, long start, long end, CancellationToken ct) => throw new NotSupportedException();
        public IRedisBatch CreateBatch() => new NoopBatch();
        public ValueTask<byte[]?> JsonGetAsync(string key, string? path, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<RedisValueLease> JsonGetLeaseAsync(string key, string? path, CancellationToken ct) => throw new NotSupportedException();
        public bool TryJsonGetLeaseAsync(string key, string? path, CancellationToken ct, out ValueTask<RedisValueLease> task) => throw new NotSupportedException();
        public ValueTask<bool> JsonSetAsync(string key, string? path, ReadOnlyMemory<byte> json, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> JsonSetLeaseAsync(string key, string? path, RedisValueLease json, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<long> JsonDelAsync(string key, string? path, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> FtCreateAsync(string index, string prefix, string[] fields, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<string[]> FtSearchAsync(string index, string query, int? offset, int? count, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> BfAddAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> BfExistsAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> TsCreateAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<long> TsAddAsync(string key, long timestamp, double value, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<(long Timestamp, double Value)[]> TsRangeAsync(string key, long from, long to, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<long> ZAddAsync(string key, double score, ReadOnlyMemory<byte> member, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<long> ZRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<long> ZCardAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<double?> ZScoreAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<long?> ZRankAsync(string key, ReadOnlyMemory<byte> member, bool descending, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<double> ZIncrByAsync(string key, double increment, ReadOnlyMemory<byte> member, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<(byte[] Member, double Score)[]> ZRangeWithScoresAsync(string key, long start, long stop, bool descending, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<(byte[] Member, double Score)[]> ZRangeByScoreWithScoresAsync(string key, double min, double max, bool descending, long? offset, long? count, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<string> ScanAsync(string? pattern = null, int pageSize = 128, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<byte[]> SScanAsync(string key, string? pattern = null, int pageSize = 128, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<(string Field, byte[] Value)> HScanAsync(string key, string? pattern = null, int pageSize = 128, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<(byte[] Member, double Score)> ZScanAsync(string key, string? pattern = null, int pageSize = 128, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopBatch : IRedisBatch
    {
        public ValueTask QueueAsync(Func<IRedisCommandExecutor, CancellationToken, ValueTask> operation, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask<T> QueueAsync<T>(Func<IRedisCommandExecutor, CancellationToken, ValueTask<T>> operation, CancellationToken ct = default)
            => ValueTask.FromResult(default(T)!);

        public ValueTask ExecuteAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestReconciliationExecutor : IRedisReconciliationExecutor
    {
        private readonly IRedisCommandExecutor _executor;

        public TestReconciliationExecutor(IRedisCommandExecutor executor)
        {
            _executor = executor;
        }

        public ValueTask<bool> SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct)
            => _executor.SetAsync(key, value, ttl, ct);

        public ValueTask<bool> DeleteAsync(string key, CancellationToken ct)
            => _executor.DeleteAsync(key, ct);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1_000_000_000; // 1s = 1e9 ticks

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public void Advance(TimeSpan by)
        {
            var delta = (long)(by.TotalSeconds * TimestampFrequency);
            Interlocked.Add(ref _timestamp, delta);
        }
    }

    private static InMemoryCacheService CreateMemoryCacheService(ICurrentCacheService current, CacheStatsRegistry statsRegistry)
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var spillOptions = Options.Create(new InMemorySpillOptions { EnableSpillToDisk = false });
        var spillStore = new FileSpillStore(spillOptions, new NoopSpillEncryptionProvider());
        return new InMemoryCacheService(memoryCache, current, statsRegistry, spillOptions, spillStore);
    }
}
