using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Sdk;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Integration;

[Collection(RedisIntegrationCollection.Name)]
public sealed class CoalescedWritesIntegrationTests
{
    [SkippableFact]
    public async Task CoalescedWrites_SingleCommand()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        await using var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        await using var exec = new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions
            {
                Connections = 1,
                MaxInFlightPerConnection = 4096,
                EnableCoalescedSocketWrites = true
            }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = cts.Token;

        var key = "vapecache:coalesce:" + Guid.NewGuid().ToString("N");
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
    public async Task CoalescedWrites_Concurrent()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        await using var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        await using var exec = new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions
            {
                Connections = 1,
                MaxInFlightPerConnection = 4096,
                EnableCoalescedSocketWrites = true
            }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;

        var keyPrefix = "vapecache:coalesce:" + Guid.NewGuid().ToString("N") + ":";

        var tasks = Enumerable.Range(0, 100).Select(async i =>
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
}
