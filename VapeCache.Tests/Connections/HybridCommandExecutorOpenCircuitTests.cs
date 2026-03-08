using System.Collections.Generic;
using LanguageExt.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.Connections;

public sealed class HybridCommandExecutorOpenCircuitTests
{
    [Fact]
    public async Task Open_circuit_routes_fallbackable_commands_to_fallback()
    {
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var breaker = new OpenBreaker();
        await using var fallback = new InMemoryCommandExecutor();
        await using var redis = CreateRedisExecutor();
        await using var sut = new HybridCommandExecutor(
            redis,
            fallback,
            breaker,
            breaker,
            stats,
            current,
            new TestOptionsMonitor<RedisCircuitBreakerOptions>(new RedisCircuitBreakerOptions { Enabled = true }),
            NullLogger<HybridCommandExecutor>.Instance);

        // Key/value
        Assert.True(await sut.SetAsync("k1", "v1"u8.ToArray(), TimeSpan.FromMinutes(1), default));
        Assert.Equal("v1", System.Text.Encoding.UTF8.GetString((await sut.GetAsync("k1", default))!));
        Assert.Equal("v1", System.Text.Encoding.UTF8.GetString((await sut.GetExAsync("k1", TimeSpan.FromMinutes(1), default))!));
        Assert.True(await sut.MSetAsync(new[] { ("k2", (ReadOnlyMemory<byte>)"v2"u8.ToArray()) }, default));
        Assert.Equal("v2", System.Text.Encoding.UTF8.GetString((await sut.MGetAsync(new[] { "k2" }, default))[0]!));
        Assert.True(await sut.DeleteAsync("k2", default));
        Assert.True(await sut.UnlinkAsync("k1", default) >= 0);
        Assert.True(await sut.TtlSecondsAsync("k1", default) <= 0);
        Assert.True(await sut.PTtlMillisecondsAsync("k1", default) <= 0);

        // Lease key/value + Try wrappers
        await sut.SetAsync("lease:key", "lease-value"u8.ToArray(), null, default);
        using (var lease = await sut.GetLeaseAsync("lease:key", default))
        {
            Assert.False(lease.IsNull);
        }
        using (var lease = await sut.GetExLeaseAsync("lease:key", null, default))
        {
            Assert.False(lease.IsNull);
        }
        Assert.True(sut.TryGetAsync("lease:key", default, out var tryGetTask));
        Assert.NotNull(await tryGetTask);
        Assert.True(sut.TryGetExAsync("lease:key", null, default, out var tryGetExTask));
        Assert.NotNull(await tryGetExTask);
        Assert.True(sut.TrySetAsync("try:set", "1"u8.ToArray(), null, default, out var trySetTask));
        Assert.True(await trySetTask);
        Assert.True(sut.TryGetLeaseAsync("lease:key", default, out var tryLeaseTask));
        using (var lease = await tryLeaseTask) { Assert.False(lease.IsNull); }
        Assert.True(sut.TryGetExLeaseAsync("lease:key", null, default, out var tryExLeaseTask));
        using (var lease = await tryExLeaseTask) { Assert.False(lease.IsNull); }

        // Hash
        Assert.Equal(1, await sut.HSetAsync("h1", "f1", "x"u8.ToArray(), default));
        Assert.Equal("x", System.Text.Encoding.UTF8.GetString((await sut.HGetAsync("h1", "f1", default))!));
        Assert.True(sut.TryHGetAsync("h1", "f1", default, out var tryHGetTask));
        Assert.NotNull(await tryHGetTask);
        var hm = await sut.HMGetAsync("h1", new[] { "f1", "missing" }, default);
        Assert.Equal(2, hm.Length);
        using (var hLease = await sut.HGetLeaseAsync("h1", "f1", default)) { Assert.False(hLease.IsNull); }

        // List + Try wrappers
        Assert.Equal(1, await sut.LPushAsync("l1", "a"u8.ToArray(), default));
        Assert.Equal(2, await sut.RPushAsync("l1", "b"u8.ToArray(), default));
        Assert.Equal(2, await sut.LLenAsync("l1", default));
        Assert.True(sut.TryLPopAsync("l1", default, out var tryLPopTask));
        Assert.NotNull(await tryLPopTask);
        Assert.True(sut.TryRPopAsync("l1", default, out var tryRPopTask));
        Assert.NotNull(await tryRPopTask);
        await sut.LPushAsync("l2", "x"u8.ToArray(), default);
        Assert.NotNull(await sut.LPopAsync("l2", default));
        await sut.RPushAsync("l3", "x"u8.ToArray(), default);
        Assert.NotNull(await sut.RPopAsync("l3", default));
        var lr = await sut.LRangeAsync("l1", 0, -1, default);
        Assert.NotNull(lr);
        await sut.RPushAsync("l4", "x"u8.ToArray(), default);
        using (var lease = await sut.LPopLeaseAsync("l4", default)) { Assert.False(lease.IsNull); }
        Assert.True(sut.TryLPopLeaseAsync("l4", default, out var tryLPopLeaseTask));
        using (var lease = await tryLPopLeaseTask) { }
        await sut.RPushAsync("l5", "x"u8.ToArray(), default);
        using (var lease = await sut.RPopLeaseAsync("l5", default)) { Assert.False(lease.IsNull); }
        Assert.True(sut.TryRPopLeaseAsync("l5", default, out var tryRPopLeaseTask));
        using (var lease = await tryRPopLeaseTask) { }

        // Set
        Assert.Equal(1, await sut.SAddAsync("s1", "m1"u8.ToArray(), default));
        Assert.True(await sut.SIsMemberAsync("s1", "m1"u8.ToArray(), default));
        Assert.True(sut.TrySIsMemberAsync("s1", "m1"u8.ToArray(), default, out var tryIsMemberTask));
        Assert.True(await tryIsMemberTask);
        Assert.Equal(1, await sut.SCardAsync("s1", default));
        Assert.Single(await sut.SMembersAsync("s1", default));
        Assert.Equal(1, await sut.SRemAsync("s1", "m1"u8.ToArray(), default));

        // Sorted set
        Assert.Equal(1, await sut.ZAddAsync("z1", 1.25, "m1"u8.ToArray(), default));
        Assert.Equal(1, await sut.ZCardAsync("z1", default));
        Assert.Equal(1.25, await sut.ZScoreAsync("z1", "m1"u8.ToArray(), default));
        Assert.Equal(0, await sut.ZRankAsync("z1", "m1"u8.ToArray(), false, default));
        Assert.True(await sut.ZIncrByAsync("z1", 1, "m1"u8.ToArray(), default) > 1);
        Assert.NotEmpty(await sut.ZRangeWithScoresAsync("z1", 0, -1, false, default));
        Assert.NotEmpty(await sut.ZRangeByScoreWithScoresAsync("z1", 0, 10, false, null, null, default));
        Assert.Equal(1, await sut.ZRemAsync("z1", "m1"u8.ToArray(), default));

        // JSON/module commands
        Assert.True(await sut.JsonSetAsync("j1", ".", "{\"a\":1}"u8.ToArray(), default));
        Assert.NotNull(await sut.JsonGetAsync("j1", ".", default));
        using (var jLease = await sut.JsonGetLeaseAsync("j1", ".", default)) { Assert.False(jLease.IsNull); }
        Assert.True(sut.TryJsonGetLeaseAsync("j1", ".", default, out var tryJsonLeaseTask));
        using (var jLease = await tryJsonLeaseTask) { Assert.False(jLease.IsNull); }
        using (var source = await sut.GetLeaseAsync("j1", default))
        {
            Assert.True(await sut.JsonSetLeaseAsync("j2", ".", source, default));
        }
        Assert.True(await sut.JsonDelAsync("j2", ".", default) >= 0);

        Assert.True(await sut.FtCreateAsync("idx", "doc:", new[] { "title" }, default));
        Assert.Empty(await sut.FtSearchAsync("idx", "*", null, null, default));
        Assert.True(await sut.BfAddAsync("bf1", "x"u8.ToArray(), default));
        Assert.True(await sut.BfExistsAsync("bf1", "x"u8.ToArray(), default));
        Assert.True(await sut.TsCreateAsync("ts1", default));
        Assert.Equal(100, await sut.TsAddAsync("ts1", 100, 1.0, default));
        Assert.Single(await sut.TsRangeAsync("ts1", 0, 200, default));

        // Streaming + server commands
        await sut.SetAsync("k-scan", "v"u8.ToArray(), null, default);
        var keys = new List<string>();
        await foreach (var k in sut.ScanAsync("k*", 10, default))
            keys.Add(k);
        Assert.NotEmpty(keys);

        await sut.HSetAsync("scan:h", "f", "v"u8.ToArray(), default);
        var hItems = new List<(string Field, byte[] Value)>();
        await foreach (var item in sut.HScanAsync("scan:h", null, 10, default))
            hItems.Add(item);
        Assert.NotEmpty(hItems);

        await sut.SAddAsync("scan:s", "v"u8.ToArray(), default);
        var sItems = new List<byte[]>();
        await foreach (var item in sut.SScanAsync("scan:s", null, 10, default))
            sItems.Add(item);
        Assert.NotEmpty(sItems);

        await sut.ZAddAsync("scan:z", 1, "m"u8.ToArray(), default);
        var zItems = new List<(byte[] Member, double Score)>();
        await foreach (var item in sut.ZScanAsync("scan:z", null, 10, default))
            zItems.Add(item);
        Assert.NotEmpty(zItems);

        Assert.Equal("PONG", await sut.PingAsync(default));

        // Extended fallback-only operations
        await sut.SetAsync("exp1", "abcdef"u8.ToArray(), null, default);
        Assert.True(await sut.ExpireAsync("exp1", TimeSpan.FromMinutes(1), default));
        await sut.RPushAsync("idx:list", "first"u8.ToArray(), default);
        Assert.Equal("first", System.Text.Encoding.UTF8.GetString((await sut.LIndexAsync("idx:list", 0, default))!));
        Assert.Equal("bc", System.Text.Encoding.UTF8.GetString((await sut.GetRangeAsync("exp1", 1, 2, default))!));

        // Batch surface
        await using (var batch = sut.CreateBatch())
        {
            await batch.QueueAsync((exec, ct) => exec.SetAsync("batch:k", "batch:v"u8.ToArray(), null, ct).AsValueTask());
            await batch.ExecuteAsync();
        }
        Assert.Equal("batch:v", System.Text.Encoding.UTF8.GetString((await sut.GetAsync("batch:k", default))!));

        Assert.Equal("memory", current.CurrentName);
        Assert.True(stats.GetOrCreate(CacheStatsNames.Hybrid).Snapshot.FallbackToMemory > 0);
    }

    private static RedisCommandExecutor CreateRedisExecutor()
    {
        var mux = new RedisMultiplexerOptions
        {
            Connections = 1,
            MaxInFlightPerConnection = 8,
            EnableCoalescedSocketWrites = false,
            EnableCommandInstrumentation = false,
            ResponseTimeout = TimeSpan.FromMilliseconds(100)
        };

        return new RedisCommandExecutor(
            new DummyFactory(),
            new TestOptionsMonitor<RedisMultiplexerOptions>(mux),
            new TestOptionsMonitor<RedisConnectionOptions>(new RedisConnectionOptions()));
    }

    private sealed class OpenBreaker : IRedisCircuitBreakerState, IRedisFailoverController
    {
        public bool Enabled => true;
        public bool IsOpen => true;
        public int ConsecutiveFailures => 0;
        public TimeSpan? OpenRemaining => TimeSpan.FromSeconds(1);
        public bool HalfOpenProbeInFlight => false;
        public bool IsForcedOpen => true;
        public string? Reason => "forced-open";

        public void ForceOpen(string reason) { }
        public void ClearForcedOpen() { }
        public void MarkRedisSuccess() { }
        public void MarkRedisFailure() { }
    }

    private sealed class DummyFactory : IRedisConnectionFactory
    {
        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
            => ValueTask.FromResult(new Result<IRedisConnection>(new InvalidOperationException("redis should not be used when open")));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal static class ValueTaskExtensions
{
    public static ValueTask AsValueTask(this ValueTask<bool> task)
        => task.IsCompletedSuccessfully ? ValueTask.CompletedTask : Await(task);

    private static async ValueTask Await(ValueTask<bool> task) => _ = await task.ConfigureAwait(false);
}
