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
            var name = _backendState.EffectiveBackend == BackendType.InMemory
                ? CacheStatsNames.Memory
                : CacheStatsNames.Redis;
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
