using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using VapeCache.Abstractions.Caching;
using VapeCache.Console.Plugins;
using VapeCache.Infrastructure.Caching;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.ConsoleHosting;

public sealed class PluginDemoHostedServiceTests
{
    [Fact]
    public async Task ExecuteAsync_runs_registered_plugins_when_enabled()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var registry = new CacheStatsRegistry();
        var spillOptions = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions { EnableSpillToDisk = false });
        var cache = new InMemoryCacheService(memory, current, registry, spillOptions, new NoopSpillStore());

        var options = new TestOptionsMonitor<PluginDemoOptions>(new PluginDemoOptions
        {
            Enabled = true,
            KeyPrefix = "plugin:test",
            Ttl = TimeSpan.FromMinutes(1)
        });

        var plugin = new CountingPlugin();
        var sut = new PluginDemoHostedService(
            new[] { plugin },
            options,
            cache,
            current,
            NullLogger<PluginDemoHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await plugin.Completed.WaitAsync(timeout.Token);
        await sut.StopAsync(CancellationToken.None);

        Assert.True(Volatile.Read(ref plugin.Calls) > 0);
        Assert.NotNull(await cache.GetAsync("plugin:test:key", CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_does_not_run_plugins_when_disabled()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var registry = new CacheStatsRegistry();
        var spillOptions = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions { EnableSpillToDisk = false });
        var cache = new InMemoryCacheService(memory, current, registry, spillOptions, new NoopSpillStore());

        var options = new TestOptionsMonitor<PluginDemoOptions>(new PluginDemoOptions
        {
            Enabled = false
        });

        var plugin = new CountingPlugin();
        var sut = new PluginDemoHostedService(
            new[] { plugin },
            options,
            cache,
            current,
            NullLogger<PluginDemoHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        Assert.Equal(0, Volatile.Read(ref plugin.Calls));
        Assert.Null(await cache.GetAsync("plugin:test:key", CancellationToken.None));
    }

    private sealed class CountingPlugin : IVapeCachePlugin
    {
        private readonly TaskCompletionSource<bool> _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls;
        public string Name => "counting-plugin";
        public Task Completed => _completed.Task;

        public async ValueTask ExecuteAsync(
            ICacheService cache,
            ICurrentCacheService current,
            CancellationToken cancellationToken)
        {
            await cache.SetAsync(
                "plugin:test:key",
                "ok"u8.ToArray(),
                new CacheEntryOptions(TimeSpan.FromMinutes(1)),
                cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref Calls);
            _completed.TrySetResult(true);
        }
    }
}
