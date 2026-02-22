using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Console.Hosting;
using VapeCache.Infrastructure.Caching;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.ConsoleHosting;

public sealed class LiveDemoHostedServiceTests
{
    [Fact]
    public async Task ExecuteAsync_writes_demo_value_when_enabled()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var registry = new CacheStatsRegistry();
        var spillOptions = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions { EnableSpillToDisk = false });
        var cache = new InMemoryCacheService(memory, current, registry, spillOptions, new NoopSpillStore());

        var options = Options.Create(new LiveDemoOptions
        {
            Enabled = true,
            Key = "demo:test",
            Interval = TimeSpan.FromMilliseconds(10),
            Ttl = TimeSpan.FromMinutes(1)
        });

        var sut = new LiveDemoHostedService(
            options,
            cache,
            current,
            circuitBreaker: null,
            NullLogger<LiveDemoHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        var wrote = await WaitUntilAsync(
            async () => await cache.GetAsync("demo:test", CancellationToken.None) is not null,
            TimeSpan.FromSeconds(2));
        await sut.StopAsync(CancellationToken.None);

        Assert.True(wrote);
        Assert.Equal("memory", current.CurrentName);
    }

    [Fact]
    public async Task ExecuteAsync_does_nothing_when_disabled()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var registry = new CacheStatsRegistry();
        var spillOptions = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions { EnableSpillToDisk = false });
        var cache = new InMemoryCacheService(memory, current, registry, spillOptions, new NoopSpillStore());

        var options = Options.Create(new LiveDemoOptions
        {
            Enabled = false,
            Key = "demo:disabled",
            Interval = TimeSpan.FromMilliseconds(10),
            Ttl = TimeSpan.FromMinutes(1)
        });

        var sut = new LiveDemoHostedService(
            options,
            cache,
            current,
            circuitBreaker: null,
            NullLogger<LiveDemoHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(30);
        await sut.StopAsync(CancellationToken.None);

        var cached = await cache.GetAsync("demo:disabled", CancellationToken.None);
        Assert.Null(cached);
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var stopAt = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < stopAt)
        {
            if (await predicate())
                return true;

            await Task.Delay(15);
        }

        return await predicate();
    }
}
