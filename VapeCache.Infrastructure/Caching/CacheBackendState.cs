using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Diagnostics;

namespace VapeCache.Infrastructure.Caching;

internal sealed class CacheBackendState(
    ICurrentCacheService current,
    IRedisCircuitBreakerState? breaker,
    IRedisFailoverController? failover) : ICacheBackendState
{
    public BackendType EffectiveBackend
    {
        get
        {
            if (failover?.IsForcedOpen == true || breaker?.IsOpen == true)
                return BackendType.InMemory;

            if (breaker is not null || failover is not null)
                return BackendType.Redis;

            return BackendTypeResolver.TryParseName(current.CurrentName, out var parsed)
                ? parsed
                : BackendType.Redis;
        }
    }
}
