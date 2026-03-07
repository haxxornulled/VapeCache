using Microsoft.Extensions.Diagnostics.HealthChecks;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Diagnostics;

namespace VapeCache.Extensions.Aspire.HealthChecks;

public sealed class VapeCacheHealthCheck : IHealthCheck
{
    private readonly ICacheService _cache;
    private readonly ICurrentCacheService _current;
    private readonly ICacheStats _stats;
    private readonly IRedisCircuitBreakerState _breaker;
    private readonly IRedisFailoverController _failover;

    public VapeCacheHealthCheck(
        ICacheService cache,
        ICurrentCacheService current,
        ICacheStats stats,
        IRedisCircuitBreakerState breaker,
        IRedisFailoverController failover)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _current = current ?? throw new ArgumentNullException(nameof(current));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
        _failover = failover ?? throw new ArgumentNullException(nameof(failover));
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var healthCheckKey = "__health__:vapecache";
            _ = await _cache.GetAsync(healthCheckKey, cancellationToken);
            var snapshot = _stats.Snapshot;
            var reads = snapshot.Hits + snapshot.Misses;
            var currentBackend = BackendTypeResolver.Resolve(
                _current.CurrentName,
                _breaker.IsOpen,
                _failover.IsForcedOpen);
            var currentBackendIsRedis = currentBackend == BackendType.Redis;
            var data = new Dictionary<string, object>
            {
                ["current_backend"] = currentBackend,
                ["current_backend_is_redis"] = currentBackendIsRedis,
                ["breaker_open"] = _breaker.IsOpen,
                ["forced_open"] = _failover.IsForcedOpen,
                ["consecutive_failures"] = _breaker.ConsecutiveFailures,
                ["get_calls"] = snapshot.GetCalls,
                ["hit_rate"] = reads <= 0 ? 0d : (double)snapshot.Hits / reads
            };

            // "current_backend" reflects the most recent operation path and may legitimately
            // be "memory" on cache misses even when Redis is healthy. Only degrade when
            // failover is active (forced-open or breaker-open).
            if (_failover.IsForcedOpen || _breaker.IsOpen)
            {
                if (_breaker.OpenRemaining is { } remaining)
                    data["open_remaining_ms"] = remaining.TotalMilliseconds;
                if (!string.IsNullOrWhiteSpace(_failover.Reason))
                    data["forced_open_reason"] = _failover.Reason;

                return HealthCheckResult.Degraded(
                    "VapeCache is operational but serving through fallback mode.",
                    data: data);
            }

            return HealthCheckResult.Healthy("VapeCache is operational.", data);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Health check cancelled.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                "VapeCache is not responding to cache operations.",
                exception: ex,
                data: new Dictionary<string, object>
                {
                    ["error_type"] = ex.GetType().Name,
                    ["error_message"] = ex.Message
                });
        }
    }
}
