using Xunit.Sdk;
using Xunit;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;
using Microsoft.Extensions.Logging.Abstractions;

namespace VapeCache.Tests.Integration;

[Collection(RedisIntegrationCollection.Name)]
public sealed class RedisConnectionFactoryIntegrationTests
{
    [SkippableFact]
    public async Task CreateAsync_connects_and_can_ping()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        await using var _ = factory.ConfigureAwait(false);

        var created = await factory.CreateAsync(CancellationToken.None);
        Assert.True(created.IsSuccess);

        await created.Match(
            async conn =>
            {
                await using var __ = conn.ConfigureAwait(false);
                await SendAuthAndSelectIfNeededAsync(conn.Stream, options, CancellationToken.None);

                var cmd = RedisResp.BuildCommand("PING");
                var sent = await conn.SendAsync(cmd, CancellationToken.None);
                Assert.True(sent.IsSuccess);

                await RedisResp.ExpectSimpleStringAsync(conn.Stream, "PONG", CancellationToken.None);
            },
            ex => throw ex);
    }

    [SkippableFact]
    public async Task CreateAsync_supports_parallel_connections()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        await using var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        var createTasks = Enumerable.Range(0, 4)
            .Select(_ => factory.CreateAsync(CancellationToken.None).AsTask())
            .ToArray();
        var results = await Task.WhenAll(createTasks);

        foreach (var result in results)
        {
            Assert.True(result.IsSuccess);
            await result.Match(
                async conn =>
                {
                    await using var __ = conn.ConfigureAwait(false);
                    await SendAuthAndSelectIfNeededAsync(conn.Stream, options, CancellationToken.None);

                    var cmd = RedisResp.BuildCommand("PING");
                    var sent = await conn.SendAsync(cmd, CancellationToken.None);
                    Assert.True(sent.IsSuccess);

                    await RedisResp.ExpectSimpleStringAsync(conn.Stream, "PONG", CancellationToken.None);
                },
                ex => throw ex);
        }
    }

    private static async Task SendAuthAndSelectIfNeededAsync(Stream stream, RedisConnectionOptions options, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(options.Password))
        {
            var auth = string.IsNullOrEmpty(options.Username)
                ? RedisResp.BuildCommand("AUTH", options.Password)
                : RedisResp.BuildCommand("AUTH", options.Username, options.Password);
            await stream.WriteAsync(auth, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            await RedisResp.ExpectSimpleStringAsync(stream, "OK", ct);
        }

        if (options.Database != 0)
        {
            var select = RedisResp.BuildCommand("SELECT", options.Database.ToString());
            await stream.WriteAsync(select, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            await RedisResp.ExpectSimpleStringAsync(stream, "OK", ct);
        }
    }
}
