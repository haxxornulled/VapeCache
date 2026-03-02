using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Sdk;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Integration;

[Collection(RedisIntegrationCollection.Name)]
public sealed class RedisCommandExecutorIntegrationTests
{
    [SkippableFact]
    public async Task Executor_can_set_get_del()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        await using var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        await using var exec = new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions { Connections = 2, MaxInFlightPerConnection = 1024 }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = cts.Token;

        var key = "vapecache:test:" + Guid.NewGuid().ToString("N");
        var bytes = "hello"u8.ToArray();

        Assert.True(await exec.SetAsync(key, bytes, TimeSpan.FromSeconds(30), ct));
        var got = await exec.GetAsync(key, ct);
        Assert.NotNull(got);
        Assert.Equal(bytes, got);

        Assert.True(await exec.DeleteAsync(key, ct));
        var missing = await exec.GetAsync(key, ct);
        Assert.Null(missing);
    }

    [SkippableFact]
    public async Task Executor_supports_getex_ttl_pttl_mget_mset_unlink()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        await using var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        await using var exec = new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions { Connections = 1, MaxInFlightPerConnection = 1024 }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = cts.Token;

        var key1 = "vapecache:ex:" + Guid.NewGuid().ToString("N");
        var key2 = "vapecache:ex:" + Guid.NewGuid().ToString("N");

        Assert.True(await exec.MSetAsync(
            new (string Key, ReadOnlyMemory<byte> Value)[]
            {
                (key1, "one"u8.ToArray()),
                (key2, "two"u8.ToArray())
            },
            ct));

        var got = await exec.MGetAsync(new[] { key1, key2 }, ct);
        Assert.Equal(2, got.Length);
        Assert.Equal("one", System.Text.Encoding.UTF8.GetString(got[0]!));
        Assert.Equal("two", System.Text.Encoding.UTF8.GetString(got[1]!));

        // GETEX sets/updates TTL and returns value
        var gotEx = await exec.GetExAsync(key1, TimeSpan.FromSeconds(5), ct);
        Assert.NotNull(gotEx);
        Assert.Equal("one", System.Text.Encoding.UTF8.GetString(gotEx!));

        var ttl = await exec.TtlSecondsAsync(key1, ct);
        var pttl = await exec.PTtlMillisecondsAsync(key1, ct);
        Assert.True(ttl >= 0);
        Assert.True(pttl >= 0);

        var unlinked = await exec.UnlinkAsync(key1, ct);
        Assert.True(unlinked >= 0);

        var missing = await exec.GetAsync(key1, ct);
        Assert.Null(missing);

        // Hashes
        var hkey = key2 + ":hash";
        var field = "f1";
        var added = await exec.HSetAsync(hkey, field, "hv"u8.ToArray(), ct);
        Assert.True(added >= 0);
        var hval = await exec.HGetAsync(hkey, field, ct);
        Assert.NotNull(hval);

        var hm = await exec.HMGetAsync(hkey, new[] { field, "missing" }, ct);
        Assert.Equal(2, hm.Length);
        Assert.NotNull(hm[0]);

        // Lists
        var lkey = key2 + ":list";
        var llen = await exec.LPushAsync(lkey, "lv"u8.ToArray(), ct);
        Assert.True(llen >= 1);
        var popped = await exec.LPopAsync(lkey, ct);
        Assert.NotNull(popped);
    }

    [SkippableFact]
    public async Task Executor_pipelines_under_concurrency()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        await using var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        await using var exec = new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions { Connections = 1, MaxInFlightPerConnection = 4096 }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;

        var keyPrefix = "vapecache:pipe:" + Guid.NewGuid().ToString("N") + ":";

        var tasks = Enumerable.Range(0, 200).Select(async i =>
        {
            var key = keyPrefix + i;
            var payload = BitConverter.GetBytes(i);
            Assert.True(await exec.SetAsync(key, payload, TimeSpan.FromSeconds(60), ct));
            var got = await exec.GetAsync(key, ct);
            Assert.NotNull(got);
            Assert.Equal(i, BitConverter.ToInt32(got));
        });

        await Task.WhenAll(tasks);
    }

    [SkippableFact]
    public async Task Executor_supports_paginated_sorted_set_ranges_by_score()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        await using var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        await using var exec = new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions { Connections = 1, MaxInFlightPerConnection = 1024 }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = cts.Token;

        var key = "vapecache:zrange:" + Guid.NewGuid().ToString("N");

        try
        {
            Assert.Equal(1, await exec.ZAddAsync(key, 1, "m1"u8.ToArray(), ct));
            Assert.Equal(1, await exec.ZAddAsync(key, 2, "m2"u8.ToArray(), ct));
            Assert.Equal(1, await exec.ZAddAsync(key, 3, "m3"u8.ToArray(), ct));

            var page = await exec.ZRangeByScoreWithScoresAsync(key, 1, 3, descending: false, offset: 1, count: 1, ct);

            Assert.Single(page);
            Assert.Equal("m2", System.Text.Encoding.UTF8.GetString(page[0].Member));
            Assert.Equal(2d, page[0].Score);
        }
        finally
        {
            await exec.UnlinkAsync(key, ct);
        }
    }

    [SkippableFact]
    public async Task Executor_supports_try_and_lease_paths()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        await using var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        await using var exec = new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions { Connections = 2, MaxInFlightPerConnection = 1024 }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = cts.Token;

        var key = "vapecache:trylease:" + Guid.NewGuid().ToString("N");
        var hashKey = key + ":hash";
        var setKey = key + ":set";
        var payload = "hello-lease"u8.ToArray();

        Assert.True(await exec.SetAsync(key, payload, TimeSpan.FromSeconds(30), ct));
        Assert.True(await exec.HSetAsync(hashKey, "f1", payload, ct) >= 0);
        Assert.True(await exec.SAddAsync(setKey, payload, ct) >= 0);

        Assert.True(exec.TryGetAsync(key, ct, out var getTask));
        var got = await getTask;
        Assert.NotNull(got);
        Assert.Equal(payload, got);

        Assert.True(exec.TryGetExAsync(key, TimeSpan.FromSeconds(30), ct, out var getExTask));
        var gotEx = await getExTask;
        Assert.NotNull(gotEx);
        Assert.Equal(payload, gotEx);

        Assert.True(exec.TryHGetAsync(hashKey, "f1", ct, out var hgetTask));
        var hget = await hgetTask;
        Assert.NotNull(hget);
        Assert.Equal(payload, hget);

        Assert.True(exec.TrySIsMemberAsync(setKey, payload, ct, out var sisMemberTask));
        Assert.True(await sisMemberTask);

        using (var lease = await exec.GetLeaseAsync(key, ct))
        {
            Assert.False(lease.IsNull);
            Assert.Equal("hello-lease", System.Text.Encoding.UTF8.GetString(lease.Span));
        }

        using (var hlease = await exec.HGetLeaseAsync(hashKey, "f1", ct))
        {
            Assert.False(hlease.IsNull);
            Assert.Equal("hello-lease", System.Text.Encoding.UTF8.GetString(hlease.Span));
        }
    }

    [SkippableFact]
    public async Task Executor_supports_scan_streams()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        await using var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        await using var exec = new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions { Connections = 1, MaxInFlightPerConnection = 1024 }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;

        var prefix = "vapecache:scan:" + Guid.NewGuid().ToString("N");
        var k1 = prefix + ":a";
        var k2 = prefix + ":b";
        var setKey = prefix + ":set";
        var hashKey = prefix + ":hash";
        var zsetKey = prefix + ":zset";

        try
        {
            Assert.True(await exec.SetAsync(k1, "v1"u8.ToArray(), TimeSpan.FromSeconds(60), ct));
            Assert.True(await exec.SetAsync(k2, "v2"u8.ToArray(), TimeSpan.FromSeconds(60), ct));

            Assert.True(await exec.SAddAsync(setKey, "m1"u8.ToArray(), ct) >= 0);
            Assert.True(await exec.SAddAsync(setKey, "m2"u8.ToArray(), ct) >= 0);

            Assert.True(await exec.HSetAsync(hashKey, "f1", "hv1"u8.ToArray(), ct) >= 0);
            Assert.True(await exec.HSetAsync(hashKey, "f2", "hv2"u8.ToArray(), ct) >= 0);

            Assert.True(await exec.ZAddAsync(zsetKey, 1.0, "z1"u8.ToArray(), ct) >= 0);
            Assert.True(await exec.ZAddAsync(zsetKey, 2.0, "z2"u8.ToArray(), ct) >= 0);

            var keys = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var key in exec.ScanAsync(prefix + "*", pageSize: 8, ct))
                keys.Add(key);
            Assert.Contains(k1, keys);
            Assert.Contains(k2, keys);
            Assert.Contains(setKey, keys);
            Assert.Contains(hashKey, keys);
            Assert.Contains(zsetKey, keys);

            var setMembers = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var member in exec.SScanAsync(setKey, pageSize: 8, ct: ct))
                setMembers.Add(System.Text.Encoding.UTF8.GetString(member));
            Assert.Contains("m1", setMembers);
            Assert.Contains("m2", setMembers);

            var hashValues = new Dictionary<string, string>(StringComparer.Ordinal);
            await foreach (var (field, value) in exec.HScanAsync(hashKey, pageSize: 8, ct: ct))
                hashValues[field] = System.Text.Encoding.UTF8.GetString(value);
            Assert.Equal("hv1", hashValues["f1"]);
            Assert.Equal("hv2", hashValues["f2"]);

            var zvalues = new Dictionary<string, double>(StringComparer.Ordinal);
            await foreach (var (member, score) in exec.ZScanAsync(zsetKey, pageSize: 8, ct: ct))
                zvalues[System.Text.Encoding.UTF8.GetString(member)] = score;
            Assert.Equal(1.0, zvalues["z1"]);
            Assert.Equal(2.0, zvalues["z2"]);
        }
        finally
        {
            await exec.UnlinkAsync(k1, ct);
            await exec.UnlinkAsync(k2, ct);
            await exec.UnlinkAsync(setKey, ct);
            await exec.UnlinkAsync(hashKey, ct);
            await exec.UnlinkAsync(zsetKey, ct);
        }
    }
}
