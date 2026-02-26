using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;
using VapeCache.Persistence;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.Caching;

public sealed class CacheChunkStreamServiceTests
{
    [Fact]
    public async Task WriteAndCopyAsync_RoundTripsLargePayload()
    {
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var memory = CreateMemoryCacheService(current, stats);
        var streams = new ChunkedCacheStreamService(memory);

        var payload = new byte[300_000];
        Random.Shared.NextBytes(payload);

        await using var source = new MemoryStream(payload, writable: false);
        var manifest = await streams.WriteAsync(
            "video:sample",
            source,
            new CacheEntryOptions(TimeSpan.FromMinutes(5)),
            new CacheChunkStreamWriteOptions
            {
                ChunkSizeBytes = 32 * 1024,
                ContentType = "video/mp4"
            },
            CancellationToken.None);

        Assert.True(manifest.ChunkCount > 1);
        Assert.Equal(32 * 1024, manifest.ChunkSizeBytes);
        Assert.Equal(payload.Length, manifest.ContentLengthBytes);
        Assert.Equal("video/mp4", manifest.ContentType);

        await using var destination = new MemoryStream();
        var copied = await streams.CopyToAsync("video:sample", destination, CancellationToken.None);
        Assert.True(copied);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public async Task CopyToAsync_UsesInMemoryFallback_WhenRedisIsForcedOpen()
    {
        var redisStore = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        var redisAvailable = true;
        var redisExecutor = CreateToggleableRedisExecutor(redisStore, () => redisAvailable);

        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var redis = new RedisCacheService(redisExecutor, current, stats);
        var memory = CreateMemoryCacheService(current, stats);

        var breaker = new TestOptionsMonitor<RedisCircuitBreakerOptions>(new RedisCircuitBreakerOptions
        {
            Enabled = true,
            ConsecutiveFailuresToOpen = 1,
            BreakDuration = TimeSpan.FromSeconds(10),
            HalfOpenProbeTimeout = TimeSpan.FromMilliseconds(100)
        });
        var failover = new TestOptionsMonitor<HybridFailoverOptions>(new HybridFailoverOptions
        {
            MirrorWritesToFallbackWhenRedisHealthy = true,
            WarmFallbackOnRedisReadHit = false,
            MaxMirrorPayloadBytes = 128 * 1024
        });

        var hybrid = new HybridCacheService(
            redis,
            memory,
            current,
            TimeProvider.System,
            breaker,
            stats,
            NullLogger<HybridCacheService>.Instance,
            failover);

        var streams = new ChunkedCacheStreamService(hybrid);
        var payload = new byte[180_000];
        Random.Shared.NextBytes(payload);

        await using (var source = new MemoryStream(payload, writable: false))
        {
            await streams.WriteAsync(
                "video:fallback",
                source,
                new CacheEntryOptions(TimeSpan.FromMinutes(5)),
                new CacheChunkStreamWriteOptions
                {
                    ChunkSizeBytes = 64 * 1024,
                    ContentType = "video/mp4"
                },
                CancellationToken.None);
        }

        redisAvailable = false;
        ((IRedisFailoverController)hybrid).ForceOpen("test-outage");

        await using var destination = new MemoryStream();
        var copied = await streams.CopyToAsync("video:fallback", destination, CancellationToken.None);
        Assert.True(copied);
        Assert.Equal(payload, destination.ToArray());
        Assert.Equal("memory", current.CurrentName);
    }

    [Fact]
    public async Task RemoveAsync_DeletesManifestAndChunks()
    {
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var memory = CreateMemoryCacheService(current, stats);
        var streams = new ChunkedCacheStreamService(memory);

        var payload = new byte[96_000];
        Random.Shared.NextBytes(payload);

        await using (var source = new MemoryStream(payload, writable: false))
        {
            await streams.WriteAsync(
                "video:remove",
                source,
                new CacheEntryOptions(TimeSpan.FromMinutes(5)),
                new CacheChunkStreamWriteOptions { ChunkSizeBytes = 16 * 1024 },
                CancellationToken.None);
        }

        var removed = await streams.RemoveAsync("video:remove", CancellationToken.None);
        Assert.True(removed);

        var manifest = await streams.GetManifestAsync("video:remove", CancellationToken.None);
        Assert.Null(manifest);

        await using var destination = new MemoryStream();
        var copied = await streams.CopyToAsync("video:remove", destination, CancellationToken.None);
        Assert.False(copied);
        Assert.Equal(0, destination.Length);
    }

    private static InMemoryCacheService CreateMemoryCacheService(ICurrentCacheService current, CacheStatsRegistry statsRegistry)
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var spillOptions = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions { EnableSpillToDisk = false });
        return new InMemoryCacheService(memoryCache, current, statsRegistry, spillOptions, new NoopSpillStore());
    }

    private static IRedisCommandExecutor CreateToggleableRedisExecutor(
        ConcurrentDictionary<string, byte[]> store,
        Func<bool> isUnavailable)
    {
        var mock = new Mock<IRedisCommandExecutor>(MockBehavior.Strict);

        mock.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string key, CancellationToken ct) =>
            {
                _ = ct;
                if (isUnavailable())
                    throw new InvalidOperationException("redis down");

                store.TryGetValue(key, out var value);
                return ValueTask.FromResult<byte[]?>(value);
            });

        mock.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Returns((string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct) =>
            {
                _ = ttl;
                _ = ct;
                if (isUnavailable())
                    throw new InvalidOperationException("redis down");

                store[key] = value.ToArray();
                return ValueTask.FromResult(true);
            });

        mock.Setup(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string key, CancellationToken ct) =>
            {
                _ = ct;
                if (isUnavailable())
                    throw new InvalidOperationException("redis down");

                return ValueTask.FromResult(store.TryRemove(key, out _));
            });

        mock.Setup(x => x.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return mock.Object;
    }
}
