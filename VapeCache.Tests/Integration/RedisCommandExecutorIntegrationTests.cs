using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Sdk;
using VapeCache.Application.Connections;
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

        var key = "vapecache:test:" + Guid.NewGuid().ToString("N");
        var bytes = "hello"u8.ToArray();

        Assert.True(await exec.SetAsync(key, bytes, TimeSpan.FromSeconds(30), CancellationToken.None));
        var got = await exec.GetAsync(key, CancellationToken.None);
        Assert.NotNull(got);
        Assert.Equal(bytes, got);

        Assert.True(await exec.DeleteAsync(key, CancellationToken.None));
        var missing = await exec.GetAsync(key, CancellationToken.None);
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

        var key1 = "vapecache:ex:" + Guid.NewGuid().ToString("N");
        var key2 = "vapecache:ex:" + Guid.NewGuid().ToString("N");

        Assert.True(await exec.MSetAsync(
            new (string Key, ReadOnlyMemory<byte> Value)[]
            {
                (key1, "one"u8.ToArray()),
                (key2, "two"u8.ToArray())
            },
            CancellationToken.None));

        var got = await exec.MGetAsync(new[] { key1, key2 }, CancellationToken.None);
        Assert.Equal(2, got.Length);
        Assert.Equal("one", System.Text.Encoding.UTF8.GetString(got[0]!));
        Assert.Equal("two", System.Text.Encoding.UTF8.GetString(got[1]!));

        // GETEX sets/updates TTL and returns value
        var gotEx = await exec.GetExAsync(key1, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.NotNull(gotEx);
        Assert.Equal("one", System.Text.Encoding.UTF8.GetString(gotEx!));

        var ttl = await exec.TtlSecondsAsync(key1, CancellationToken.None);
        var pttl = await exec.PTtlMillisecondsAsync(key1, CancellationToken.None);
        Assert.True(ttl >= 0);
        Assert.True(pttl >= 0);

        var unlinked = await exec.UnlinkAsync(key1, CancellationToken.None);
        Assert.True(unlinked >= 0);

        var missing = await exec.GetAsync(key1, CancellationToken.None);
        Assert.Null(missing);
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

        var keyPrefix = "vapecache:pipe:" + Guid.NewGuid().ToString("N") + ":";

        var tasks = Enumerable.Range(0, 200).Select(async i =>
        {
            var key = keyPrefix + i;
            var payload = BitConverter.GetBytes(i);
            Assert.True(await exec.SetAsync(key, payload, TimeSpan.FromSeconds(60), CancellationToken.None));
            var got = await exec.GetAsync(key, CancellationToken.None);
            Assert.NotNull(got);
            Assert.Equal(i, BitConverter.ToInt32(got));
        });

        await Task.WhenAll(tasks);
    }
}
