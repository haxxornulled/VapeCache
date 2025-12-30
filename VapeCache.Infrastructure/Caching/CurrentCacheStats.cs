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
            return _registry.TryGet(name, out var stats)
                ? stats.Snapshot
                : default;
        }
    }
}
