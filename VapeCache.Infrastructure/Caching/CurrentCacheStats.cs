using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Diagnostics;

namespace VapeCache.Infrastructure.Caching;

internal sealed class CurrentCacheStats : ICacheStats
{
    private readonly ICacheBackendState _backendState;
    private readonly CacheStatsRegistry _registry;

    public CurrentCacheStats(ICacheBackendState backendState, CacheStatsRegistry registry)
    {
        _backendState = backendState;
        _registry = registry;
    }

    public CacheStatsSnapshot Snapshot
    {
        get
        {
            if (_registry.TryGet(CacheStatsNames.Hybrid, out var hybridStats))
                return hybridStats.Snapshot;

            var name = _backendState.EffectiveBackend == BackendType.InMemory
                ? CacheStatsNames.Memory
                : CacheStatsNames.Redis;

            return _registry.TryGet(name, out var currentStats)
                ? currentStats.Snapshot
                : default;
        }
    }
}
