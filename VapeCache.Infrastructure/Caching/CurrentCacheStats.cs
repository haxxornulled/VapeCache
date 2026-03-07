using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching;

internal sealed class CurrentCacheStats : ICacheStats
{
    private readonly ICurrentCacheService _current;
    private readonly CacheStatsRegistry _registry;

    public CurrentCacheStats(ICurrentCacheService current, CacheStatsRegistry registry)
    {
        _current = current;
        _registry = registry;
    }

    public CacheStatsSnapshot Snapshot
    {
        get
        {
            var name = _current.CurrentName;
            var current = _registry.TryGet(name, out var currentStats)
                ? currentStats.Snapshot
                : default;

            if (!_registry.TryGet(CacheStatsNames.Hybrid, out var hybridStats))
            {
                return current;
            }

            var hybrid = hybridStats.Snapshot;
            return current with
            {
                FallbackToMemory = hybrid.FallbackToMemory,
                RedisBreakerOpened = hybrid.RedisBreakerOpened,
                StampedeKeyRejected = hybrid.StampedeKeyRejected,
                StampedeLockWaitTimeout = hybrid.StampedeLockWaitTimeout,
                StampedeFailureBackoffRejected = hybrid.StampedeFailureBackoffRejected
            };
        }
    }
}
