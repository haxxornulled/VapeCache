namespace VapeCache.Application.Caching;

public interface ICacheStats
{
    CacheStatsSnapshot Snapshot { get; }
}

public readonly record struct CacheStatsSnapshot(
    long GetCalls,
    long Hits,
    long Misses,
    long SetCalls,
    long RemoveCalls,
    long FallbackToMemory,
    long RedisBreakerOpened);

