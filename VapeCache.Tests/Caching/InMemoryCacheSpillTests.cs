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

    [Fact]
    public async Task InMemoryCache_DoesNotCreateUnreadableSpillEntries_WithNoopStore()
    {
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            EnableSpillToDisk = true,
            SpillThresholdBytes = 16,
            InlinePrefixBytes = 4
        });

        var cache = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var service = new InMemoryCacheService(cache, current, stats, options, new NoopSpillStore());

        var payload = new byte[64];
        Random.Shared.NextBytes(payload);

        await service.SetAsync("noop-spill:key", payload, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);
        var fetched = await service.GetAsync("noop-spill:key", CancellationToken.None);

        Assert.NotNull(fetched);
        Assert.Equal(payload, fetched);
    }

    [Fact]
    public async Task RemoveAsync_ReturnsFalse_WhenEntryDoesNotExist()
    {
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            EnableSpillToDisk = false
        });

        var cache = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var service = new InMemoryCacheService(cache, current, stats, options, new NoopSpillStore());

        var removed = await service.RemoveAsync("missing:key", CancellationToken.None);
        Assert.False(removed);
    }

    [Fact]
    public async Task InMemoryCache_IsolatesStoredBuffers_FromCallerAndReaderMutations()
    {
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            EnableSpillToDisk = false
        });

        var cache = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var service = new InMemoryCacheService(cache, current, stats, options, new NoopSpillStore());

        var source = new byte[] { 1, 2, 3, 4 };
        await service.SetAsync("copy:key", source, default, CancellationToken.None);

        source[0] = 99;

        var firstRead = await service.GetAsync("copy:key", CancellationToken.None);
        Assert.NotNull(firstRead);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, firstRead);

        firstRead![1] = 88;

        var secondRead = await service.GetAsync("copy:key", CancellationToken.None);
        Assert.NotNull(secondRead);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, secondRead);
    }

    [Fact]
    public async Task OverwritingSpilledEntry_LeavesSingleSpillFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "vapecache-spill-overwrite", Guid.NewGuid().ToString("n"));
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

        try
        {
            var payload1 = new byte[64];
            var payload2 = new byte[96];
            Random.Shared.NextBytes(payload1);
            Random.Shared.NextBytes(payload2);

            await service.SetAsync("spill:overwrite", payload1, default, CancellationToken.None);
            await service.SetAsync("spill:overwrite", payload2, default, CancellationToken.None);
            var fetched = await service.GetAsync("spill:overwrite", CancellationToken.None);

            Assert.NotNull(fetched);
            Assert.Equal(payload2, fetched);

            var files = Directory.GetFiles(root, "*.bin", SearchOption.AllDirectories);
            Assert.Single(files);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
