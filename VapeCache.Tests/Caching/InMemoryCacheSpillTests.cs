using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;
using VapeCache.Persistence;
using VapeCache.Tests.Infrastructure;
using Xunit;

namespace VapeCache.Tests.Caching;

public sealed class InMemoryCacheSpillTests
{
    [Fact]
    public async Task InMemoryCache_SpillsLargeValuesToDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), "vapecache-spill", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            EnableSpillToDisk = true,
            SpillThresholdBytes = 16,
            InlinePrefixBytes = 4,
            SpillDirectory = root
        });

        var spillStore = new FileSpillStore(options, new NoopSpillEncryptionProvider());
        var cache = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var service = new InMemoryCacheService(cache, current, stats, options, spillStore);

        var payload = new byte[64];
        Random.Shared.NextBytes(payload);

        try
        {
            await service.SetAsync("spill:key", payload, default, CancellationToken.None);
            var fetched = await service.GetAsync("spill:key", CancellationToken.None);

            Assert.NotNull(fetched);
            Assert.Equal(payload, fetched);

            var files = Directory.GetFiles(root, "*.bin", SearchOption.AllDirectories);
            Assert.NotEmpty(files);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
