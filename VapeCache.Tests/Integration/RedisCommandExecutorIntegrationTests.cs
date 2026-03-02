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
}
