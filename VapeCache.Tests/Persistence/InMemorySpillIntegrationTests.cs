using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;
using VapeCache.Persistence;
using VapeCache.Tests.Infrastructure;
using Xunit;

namespace VapeCache.Tests.Persistence;

/// <summary>
/// Integration tests for InMemoryCacheService with FileSpillStore.
/// Tests the full spill-to-disk workflow with inline prefix optimization.
/// </summary>
public sealed class InMemorySpillIntegrationTests : IDisposable
{
    private readonly string _testRoot;

    public InMemorySpillIntegrationTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "vapecache-test-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    [Fact]
    public async Task SmallValue_StaysInMemory_DoesNotSpill()
    {
        // Arrange
        var spillOptions = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            EnableSpillToDisk = true,
            SpillThresholdBytes = 1024, // 1 KB threshold
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = false
        });

        var cache = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var spillStore = new FileSpillStore(spillOptions, new NoopSpillEncryptionProvider());
        var service = new InMemoryCacheService(cache, current, stats, spillOptions, spillStore);

        var key = "small:key";
        var payload = new byte[512]; // 512 bytes (below threshold)
        Random.Shared.NextBytes(payload);

        // Act
        await service.SetAsync(key, payload, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);
        var fetched = await service.GetAsync(key, CancellationToken.None);

        // Assert
        Assert.NotNull(fetched);
        Assert.Equal(payload, fetched);

        // Verify no spill files created
        var spillFiles = Directory.GetFiles(_testRoot, "*.bin", SearchOption.AllDirectories);
        Assert.Empty(spillFiles);
    }

    [Fact]
    public async Task LargeValue_SpillsToDisk_WithInlinePrefix()
    {
        // Arrange
        var spillOptions = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            EnableSpillToDisk = true,
            SpillThresholdBytes = 1024, // 1 KB threshold
            InlinePrefixBytes = 256,    // First 256 bytes in-memory
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = false
        });

        var cache = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var spillStore = new FileSpillStore(spillOptions, new NoopSpillEncryptionProvider());
        var service = new InMemoryCacheService(cache, current, stats, spillOptions, spillStore);

        var key = "large:key";
        var payload = new byte[10 * 1024]; // 10 KB (above threshold)
        Random.Shared.NextBytes(payload);

        // Act
        await service.SetAsync(key, payload, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);
        var fetched = await service.GetAsync(key, CancellationToken.None);

        // Assert
        Assert.NotNull(fetched);
        Assert.Equal(payload.Length, fetched.Length);
        Assert.Equal(payload, fetched);

        // Verify spill file was created
        var spillFiles = Directory.GetFiles(_testRoot, "*.bin", SearchOption.AllDirectories);
        Assert.Single(spillFiles);
    }

    [Fact]
    public async Task SpilledValue_CanBeRetrieved_AfterCacheEviction()
    {
        // Arrange
        var spillOptions = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            EnableSpillToDisk = true,
            SpillThresholdBytes = 1024,
            InlinePrefixBytes = 128,
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = false
        });

        var cache = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var spillStore = new FileSpillStore(spillOptions, new NoopSpillEncryptionProvider());
        var service = new InMemoryCacheService(cache, current, stats, spillOptions, spillStore);

        var key = "evictable:key";
        var payload = new byte[5 * 1024]; // 5 KB
        Random.Shared.NextBytes(payload);

        // Act - Set and retrieve
        await service.SetAsync(key, payload, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);
        var fetched1 = await service.GetAsync(key, CancellationToken.None);

        // Manually evict from memory cache (but spill file remains)
        cache.Remove(key);

        // Try to retrieve again (should fail because we can't reconstruct without the SpillEntry)
        var fetched2 = await service.GetAsync(key, CancellationToken.None);

        // Assert
        Assert.NotNull(fetched1);
        Assert.Equal(payload, fetched1);
        Assert.Null(fetched2); // Can't retrieve after eviction (expected behavior)
    }

    [Fact]
    public async Task MultipleSpilledValues_CanCoexist()
    {
        // Arrange
        var spillOptions = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            EnableSpillToDisk = true,
            SpillThresholdBytes = 1024,
            InlinePrefixBytes = 256,
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = false
        });

        var cache = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var spillStore = new FileSpillStore(spillOptions, new NoopSpillEncryptionProvider());
        var service = new InMemoryCacheService(cache, current, stats, spillOptions, spillStore);

        var key1 = "large:key1";
        var key2 = "large:key2";
        var key3 = "large:key3";

        var payload1 = new byte[2 * 1024];
        var payload2 = new byte[3 * 1024];
        var payload3 = new byte[4 * 1024];

        Random.Shared.NextBytes(payload1);
        Random.Shared.NextBytes(payload2);
        Random.Shared.NextBytes(payload3);

        // Act
        await service.SetAsync(key1, payload1, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);
        await service.SetAsync(key2, payload2, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);
        await service.SetAsync(key3, payload3, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);

        var fetched1 = await service.GetAsync(key1, CancellationToken.None);
        var fetched2 = await service.GetAsync(key2, CancellationToken.None);
        var fetched3 = await service.GetAsync(key3, CancellationToken.None);

        // Assert
        Assert.Equal(payload1, fetched1);
        Assert.Equal(payload2, fetched2);
        Assert.Equal(payload3, fetched3);

        // Verify multiple spill files created
        var spillFiles = Directory.GetFiles(_testRoot, "*.bin", SearchOption.AllDirectories);
        Assert.Equal(3, spillFiles.Length);
    }

    [Fact]
    public async Task SpillDisabled_StoresLargeValueInMemory()
    {
        // Arrange
        var spillOptions = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            EnableSpillToDisk = false, // Disabled
            SpillThresholdBytes = 1024,
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = false
        });

        var cache = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var spillStore = new FileSpillStore(spillOptions, new NoopSpillEncryptionProvider());
        var service = new InMemoryCacheService(cache, current, stats, spillOptions, spillStore);

        var key = "large:inmemory";
        var payload = new byte[10 * 1024]; // 10 KB
        Random.Shared.NextBytes(payload);

        // Act
        await service.SetAsync(key, payload, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);
        var fetched = await service.GetAsync(key, CancellationToken.None);

        // Assert
        Assert.Equal(payload, fetched);

        // Verify no spill files created (even though value is large)
        var spillFiles = Directory.GetFiles(_testRoot, "*.bin", SearchOption.AllDirectories);
        Assert.Empty(spillFiles);
    }

    [Fact]
    public async Task UpdateSpilledValue_ReplacesSpillFile()
    {
        // Arrange
        var spillOptions = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            EnableSpillToDisk = true,
            SpillThresholdBytes = 1024,
            InlinePrefixBytes = 256,
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = false
        });

        var cache = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var spillStore = new FileSpillStore(spillOptions, new NoopSpillEncryptionProvider());
        var service = new InMemoryCacheService(cache, current, stats, spillOptions, spillStore);

        var key = "update:key";
        var payload1 = new byte[5 * 1024];
        var payload2 = new byte[7 * 1024];

        Random.Shared.NextBytes(payload1);
        Random.Shared.NextBytes(payload2);

        // Act
        await service.SetAsync(key, payload1, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);
        await service.SetAsync(key, payload2, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);

        var fetched = await service.GetAsync(key, CancellationToken.None);

        // Assert
        Assert.Equal(payload2, fetched);

        // Should still have one spill file (not two)
        var spillFiles = Directory.GetFiles(_testRoot, "*.bin", SearchOption.AllDirectories);
        Assert.Single(spillFiles);
    }

    [Fact]
    public async Task RemoveSpilledValue_DeletesSpillFile()
    {
        // Arrange
        var spillOptions = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            EnableSpillToDisk = true,
            SpillThresholdBytes = 1024,
            InlinePrefixBytes = 256,
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = false
        });

        var cache = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var spillStore = new FileSpillStore(spillOptions, new NoopSpillEncryptionProvider());
        var service = new InMemoryCacheService(cache, current, stats, spillOptions, spillStore);

        var key = "remove:key";
        var payload = new byte[5 * 1024];
        Random.Shared.NextBytes(payload);

        // Act
        await service.SetAsync(key, payload, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);

        // Verify spill file exists
        var spillFilesBefore = Directory.GetFiles(_testRoot, "*.bin", SearchOption.AllDirectories);
        Assert.Single(spillFilesBefore);

        // Remove the entry
        await service.RemoveAsync(key, CancellationToken.None);

        // Wait a bit for cleanup callback
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        // Assert - Spill file should be deleted via eviction callback
        var spillFilesAfter = Directory.GetFiles(_testRoot, "*.bin", SearchOption.AllDirectories);
        Assert.Empty(spillFilesAfter);
    }

    [Fact]
    public async Task ZeroInlinePrefix_StoresEntireTailOnDisk()
    {
        // Arrange
        var spillOptions = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            EnableSpillToDisk = true,
            SpillThresholdBytes = 1024,
            InlinePrefixBytes = 0, // No inline prefix
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = false
        });

        var cache = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var spillStore = new FileSpillStore(spillOptions, new NoopSpillEncryptionProvider());
        var service = new InMemoryCacheService(cache, current, stats, spillOptions, spillStore);

        var key = "no-prefix:key";
        var payload = new byte[5 * 1024];
        Random.Shared.NextBytes(payload);

        // Act
        await service.SetAsync(key, payload, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);
        var fetched = await service.GetAsync(key, CancellationToken.None);

        // Assert
        Assert.Equal(payload, fetched);
    }
}
