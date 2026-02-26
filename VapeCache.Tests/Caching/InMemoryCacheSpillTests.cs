using Microsoft.Extensions.Caching.Memory;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;
using VapeCache.Tests.Infrastructure;
using Xunit;

namespace VapeCache.Tests.Caching;

public sealed class InMemoryCacheSpillTests
{
    [Fact]
    public async Task InMemoryCache_LargeValues_RoundTripWithNoopSpillStore()
    {
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            EnableSpillToDisk = false,
            SpillThresholdBytes = 16,
            InlinePrefixBytes = 4
        });

        var cache = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var service = new InMemoryCacheService(cache, current, stats, options, new NoopSpillStore());

        var payload = new byte[64];
        Random.Shared.NextBytes(payload);

        await service.SetAsync("spill:key", payload, default, CancellationToken.None);
        var fetched = await service.GetAsync("spill:key", CancellationToken.None);

        Assert.NotNull(fetched);
        Assert.Equal(payload, fetched);
    }
}
