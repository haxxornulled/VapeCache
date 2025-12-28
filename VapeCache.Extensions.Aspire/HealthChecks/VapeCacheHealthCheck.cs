using Microsoft.Extensions.Diagnostics.HealthChecks;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Extensions.Aspire.HealthChecks;

public sealed class VapeCacheHealthCheck : IHealthCheck
{
    private readonly ICacheService _cache;

    public VapeCacheHealthCheck(ICacheService cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var healthCheckKey = "__health__:vapecache";
            _ = await _cache.GetAsync(healthCheckKey, cancellationToken);
            return HealthCheckResult.Healthy("VapeCache is operational.");
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
