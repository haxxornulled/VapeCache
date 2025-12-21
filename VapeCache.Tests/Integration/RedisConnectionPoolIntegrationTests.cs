using LanguageExt.Common;
using Xunit;
using Xunit.Sdk;
using VapeCache.Application.Connections;
using VapeCache.Infrastructure.Connections;
using Microsoft.Extensions.Logging.Abstractions;

namespace VapeCache.Tests.Integration;

[Collection(RedisIntegrationCollection.Name)]
public sealed class RedisConnectionPoolIntegrationTests
{
    [SkippableFact]
    public async Task Pool_warms_and_reuses_connections()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        options = options with
        {
            MaxConnections = 2,
            MaxIdle = 2,
            Warm = 2,
            AcquireTimeout = TimeSpan.FromSeconds(3),
            ConnectTimeout = TimeSpan.FromSeconds(3)
        };

        await using var realFactory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());
        var countingFactory = new CountingFactory(realFactory);

        await using var pool = new RedisConnectionPool(countingFactory, RedisIntegrationConfig.Monitor(options), NullLogger<RedisConnectionPool>.Instance);

        await WaitUntilAsync(() => countingFactory.CreatedCount >= 2, TimeSpan.FromSeconds(5));
        Assert.True(countingFactory.CreatedCount <= 2);

        var r1 = await pool.RentAsync(CancellationToken.None);
        var r2 = await pool.RentAsync(CancellationToken.None);
        Assert.True(r1.IsSuccess);
        Assert.True(r2.IsSuccess);

        await PingAsync(r1, options);
        await PingAsync(r2, options);

        await r1.Match(async l => await l.DisposeAsync(), ex => throw ex);
        await r2.Match(async l => await l.DisposeAsync(), ex => throw ex);

        var r3 = await pool.RentAsync(CancellationToken.None);
        Assert.True(r3.IsSuccess);
        await PingAsync(r3, options);
        await r3.Match(async l => await l.DisposeAsync(), ex => throw ex);

        Assert.True(countingFactory.CreatedCount <= 2);
    }

    [SkippableFact]
    public async Task Pool_does_not_exceed_capacity_under_concurrency()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        options = options with
        {
            MaxConnections = 2,
            MaxIdle = 2,
            Warm = 0,
            AcquireTimeout = TimeSpan.FromSeconds(5),
            ConnectTimeout = TimeSpan.FromSeconds(3)
        };

        await using var realFactory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());
        var countingFactory = new CountingFactory(realFactory);
        await using var pool = new RedisConnectionPool(countingFactory, RedisIntegrationConfig.Monitor(options), NullLogger<RedisConnectionPool>.Instance);

        var leases = await Task.WhenAll(
            RentAndHoldAsync(pool, options, holdMs: 250),
            RentAndHoldAsync(pool, options, holdMs: 250),
            RentAndHoldAsync(pool, options, holdMs: 250),
            RentAndHoldAsync(pool, options, holdMs: 250)
        );

        foreach (var lease in leases)
        {
            Assert.True(lease.IsSuccess);
            await lease.Match(async l => await l.DisposeAsync(), ex => throw ex);
        }

        Assert.Equal(2, countingFactory.CreatedCount);
    }

    private static async Task<Result<IRedisConnectionLease>> RentAndHoldAsync(
        RedisConnectionPool pool,
        RedisConnectionOptions options,
        int holdMs)
    {
        var leaseResult = await pool.RentAsync(CancellationToken.None);
        if (!leaseResult.IsSuccess) return leaseResult;

        await leaseResult.Match(
            async lease =>
            {
                await SendAuthAndSelectIfNeededAsync(lease.Connection.Stream, options, CancellationToken.None);
                var cmd = RedisResp.BuildCommand("PING");
                await lease.Connection.Stream.WriteAsync(cmd, CancellationToken.None);
                await lease.Connection.Stream.FlushAsync(CancellationToken.None);
                await RedisResp.ExpectSimpleStringAsync(lease.Connection.Stream, "PONG", CancellationToken.None);
                await Task.Delay(holdMs);
            },
            _ => Task.CompletedTask);

        return leaseResult;
    }

    private static async Task PingAsync(Result<IRedisConnectionLease> leaseResult, RedisConnectionOptions options)
    {
        await leaseResult.Match(
            async lease =>
            {
                await SendAuthAndSelectIfNeededAsync(lease.Connection.Stream, options, CancellationToken.None);
                var cmd = RedisResp.BuildCommand("PING");
                await lease.Connection.Stream.WriteAsync(cmd, CancellationToken.None);
                await lease.Connection.Stream.FlushAsync(CancellationToken.None);
                await RedisResp.ExpectSimpleStringAsync(lease.Connection.Stream, "PONG", CancellationToken.None);
            },
            ex => throw ex);
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

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            if (predicate()) return;
            await Task.Delay(50);
        }
    }

    private sealed class CountingFactory(IRedisConnectionFactory inner) : IRedisConnectionFactory
    {
        private int _created;
        public int CreatedCount => Volatile.Read(ref _created);

        public async ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
        {
            var r = await inner.CreateAsync(ct);
            if (r.IsSuccess) Interlocked.Increment(ref _created);
            return r;
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
