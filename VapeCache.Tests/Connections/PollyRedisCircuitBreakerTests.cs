using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.Connections;

public sealed class PollyRedisCircuitBreakerTests
{
    [Fact]
    public async Task ExecuteAsync_passthrough_when_disabled()
    {
        var options = new TestOptionsMonitor<RedisCircuitBreakerOptions>(new RedisCircuitBreakerOptions
        {
            Enabled = false
        });
        var registry = new CacheStatsRegistry();
        var sut = new PollyRedisCircuitBreaker(options, registry, NullLogger<PollyRedisCircuitBreaker>.Instance);

        var result = await sut.ExecuteAsync(static _ => ValueTask.FromResult(42), CancellationToken.None);

        Assert.Equal(42, result);
        Assert.False(sut.IsOpen);
    }

    [Fact]
    public async Task ForceOpen_blocks_execute_until_cleared()
    {
        var options = new TestOptionsMonitor<RedisCircuitBreakerOptions>(new RedisCircuitBreakerOptions
        {
            Enabled = true,
            ConsecutiveFailuresToOpen = 2,
            BreakDuration = TimeSpan.FromMilliseconds(500)
        });
        var registry = new CacheStatsRegistry();
        var sut = new PollyRedisCircuitBreaker(options, registry, NullLogger<PollyRedisCircuitBreaker>.Instance);

        sut.ForceOpen("maintenance");
        Assert.True(sut.IsForcedOpen);
        Assert.True(sut.IsOpen);
        Assert.Equal("maintenance", sut.Reason);

        await Assert.ThrowsAsync<BrokenCircuitException>(() =>
            sut.ExecuteAsync(static _ => ValueTask.FromResult(1), CancellationToken.None).AsTask());

        sut.ClearForcedOpen();
        var ok = await sut.ExecuteAsync(static _ => ValueTask.FromResult(1), CancellationToken.None);
        Assert.Equal(1, ok);
    }

    [Fact]
    public async Task ExecuteAsync_can_open_circuit_after_repeated_failures()
    {
        var options = new TestOptionsMonitor<RedisCircuitBreakerOptions>(new RedisCircuitBreakerOptions
        {
            Enabled = true,
            ConsecutiveFailuresToOpen = 2,
            BreakDuration = TimeSpan.FromSeconds(1)
        });
        var registry = new CacheStatsRegistry();
        var sut = new PollyRedisCircuitBreaker(options, registry, NullLogger<PollyRedisCircuitBreaker>.Instance);

        var sawBrokenCircuit = false;
        for (var i = 0; i < 8; i++)
        {
            try
            {
                await sut.ExecuteAsync<int>(
                    static _ => ValueTask.FromException<int>(new InvalidOperationException("fail")),
                    CancellationToken.None);
            }
            catch (BrokenCircuitException)
            {
                sawBrokenCircuit = true;
                break;
            }
            catch (InvalidOperationException)
            {
            }
        }

        Assert.True(sawBrokenCircuit || sut.IsOpen);
        Assert.True(registry.GetOrCreate(CacheStatsNames.Hybrid).Snapshot.RedisBreakerOpened >= 1);
    }
}
