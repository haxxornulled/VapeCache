using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using VapeCache.Abstractions.Caching;
using VapeCache.Console.GroceryStore;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Caching.Codecs;
using VapeCache.Infrastructure.Collections;
using VapeCache.Infrastructure.Connections;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.Console;

public sealed class GroceryStoreComparisonStressTestTests
{
    [Fact]
    public async Task RunStressTestAsync_returns_metrics_for_small_workload()
    {
        await using var h = Harness.Create();
        var sut = new GroceryStoreComparisonStressTest(h.Service, NullLogger<GroceryStoreComparisonStressTest>.Instance, "VapeCache");

        var result = await sut.RunStressTestAsync(shopperCount: 40, maxCartSize: 15);

        Assert.Equal("VapeCache", result.ProviderName);
        Assert.Equal(40, result.ShopperCount);
        Assert.Equal(40, result.SuccessCount + result.ErrorCount);
        Assert.True(result.ThroughputShoppersPerSec > 0);
        Assert.True(result.P99LatencyMs >= result.P95LatencyMs);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly InMemoryCommandExecutor _executor;
        private readonly MemoryCache _memoryCache;

        public GroceryStoreService Service { get; }

        private Harness(GroceryStoreService service, InMemoryCommandExecutor executor, MemoryCache memoryCache)
        {
            Service = service;
            _executor = executor;
            _memoryCache = memoryCache;
        }

        public static Harness Create()
        {
            var executor = new InMemoryCommandExecutor();
            var codecs = new SystemTextJsonCodecProvider();
            var collections = new CacheCollectionFactory(executor, codecs);

            var current = new CurrentCacheService();
            var stats = new CacheStatsRegistry();
            var memory = new MemoryCache(new MemoryCacheOptions());
            var spillOptions = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions { EnableSpillToDisk = false });
            var cacheService = new InMemoryCacheService(memory, current, stats, spillOptions, new NoopSpillStore());
            var client = new VapeCacheClient(cacheService, codecs);

            var service = new GroceryStoreService(collections, client, executor, NullLogger<GroceryStoreService>.Instance);
            return new Harness(service, executor, memory);
        }

        public async ValueTask DisposeAsync()
        {
            _memoryCache.Dispose();
            await _executor.DisposeAsync();
        }
    }
}
