using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Diagnostics;

namespace VapeCache.Infrastructure.Caching;

internal sealed class CacheBackendState : ICacheBackendState
{
    private readonly ICurrentCacheService _current;
    private readonly IRedisCircuitBreakerState? _breaker;
    private readonly IRedisFailoverController? _failover;

    public CacheBackendState(
        ICurrentCacheService current,
        IRedisCircuitBreakerState? breaker,
        IRedisFailoverController? failover)
    {
        _current = current;
        _breaker = breaker;
        _failover = failover;
    }

    public BackendType EffectiveBackend
    {
        get
        {
            if (_failover?.IsForcedOpen == true || _breaker?.IsOpen == true)
                return BackendType.InMemory;

            if (BackendTypeResolver.TryParseName(_current.CurrentName, out var parsed))
                return parsed;

            return BackendType.Redis;
        }
    }
}
