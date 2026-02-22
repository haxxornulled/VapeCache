using VapeCache.Infrastructure.Caching;

namespace VapeCache.Tests.Caching;

public sealed class CurrentCacheStatsTests
{
    [Fact]
    public void Snapshot_returns_default_when_current_backend_has_no_stats()
    {
        var current = new CurrentCacheService();
        var registry = new CacheStatsRegistry();
        var sut = new CurrentCacheStats(current, registry);

        current.SetCurrent("nonexistent");
        var snapshot = sut.Snapshot;

        Assert.Equal(0, snapshot.GetCalls);
        Assert.Equal(0, snapshot.Hits);
        Assert.Equal(0, snapshot.Misses);
    }

    [Fact]
    public void Snapshot_reads_stats_from_current_backend()
    {
        var current = new CurrentCacheService();
        var registry = new CacheStatsRegistry();
        var memoryStats = registry.GetOrCreate("memory");
        memoryStats.IncGet();
        memoryStats.IncHit();

        var redisStats = registry.GetOrCreate("redis");
        redisStats.IncGet();
        redisStats.IncMiss();

        var sut = new CurrentCacheStats(current, registry);

        current.SetCurrent("memory");
        var memory = sut.Snapshot;
        Assert.Equal(1, memory.GetCalls);
        Assert.Equal(1, memory.Hits);
        Assert.Equal(0, memory.Misses);

        current.SetCurrent("redis");
        var redis = sut.Snapshot;
        Assert.Equal(1, redis.GetCalls);
        Assert.Equal(0, redis.Hits);
        Assert.Equal(1, redis.Misses);
    }
}
