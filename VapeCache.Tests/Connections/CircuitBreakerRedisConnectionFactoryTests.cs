using LanguageExt.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.Connections;

public sealed class CircuitBreakerRedisConnectionFactoryTests
{
    [Fact]
    public async Task CreateAsync_disabled_circuit_returns_inner_failure()
    {
        await using var inner = CreateInnerFactory();
        var registry = new CacheStatsRegistry();
        await using var sut = new CircuitBreakerRedisConnectionFactory(
            inner,
            new TestOptionsMonitor<RedisCircuitBreakerOptions>(new RedisCircuitBreakerOptions
            {
                Enabled = false,
                ConsecutiveFailuresToOpen = 2,
                BreakDuration = TimeSpan.FromMilliseconds(200)
            }),
            registry,
            NullLogger<CircuitBreakerRedisConnectionFactory>.Instance);

        var result = await sut.CreateAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.IsNotType<BrokenCircuitException>(GetError(result));
    }

    [Fact]
    public async Task CreateAsync_opens_circuit_after_repeated_failures()
    {
        await using var inner = CreateInnerFactory();
        var registry = new CacheStatsRegistry();
        await using var sut = new CircuitBreakerRedisConnectionFactory(
            inner,
            new TestOptionsMonitor<RedisCircuitBreakerOptions>(new RedisCircuitBreakerOptions
            {
                Enabled = true,
                ConsecutiveFailuresToOpen = 2,
                BreakDuration = TimeSpan.FromSeconds(1),
                UseExponentialBackoff = false
            }),
            registry,
            NullLogger<CircuitBreakerRedisConnectionFactory>.Instance);

        var sawBrokenCircuit = false;
        for (var i = 0; i < 12; i++)
        {
            var result = await sut.CreateAsync(CancellationToken.None);
            var error = GetError(result);
            if (error is BrokenCircuitException)
            {
                sawBrokenCircuit = true;
                break;
            }
        }

        Assert.True(sawBrokenCircuit);
        Assert.True(registry.GetOrCreate(CacheStatsNames.Hybrid).Snapshot.RedisBreakerOpened >= 1);
    }

    private static RedisConnectionFactory CreateInnerFactory()
    {
        var options = new TestOptionsMonitor<RedisConnectionOptions>(new RedisConnectionOptions
        {
            Host = "127.0.0.1",
            Port = 1,
            ConnectTimeout = TimeSpan.FromMilliseconds(30),
            UseTls = false
        });

        return new RedisConnectionFactory(
            options,
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());
    }

    private static Exception? GetError(Result<IRedisConnection> result)
        => result.Match(static _ => (Exception?)null, static ex => ex);
}
