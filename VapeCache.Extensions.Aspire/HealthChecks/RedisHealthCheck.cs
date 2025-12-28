using Microsoft.Extensions.Diagnostics.HealthChecks;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Extensions.Aspire.HealthChecks;

public sealed class RedisHealthCheck : IHealthCheck
{
    private readonly IRedisConnectionPool _pool;

    public RedisHealthCheck(IRedisConnectionPool pool)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _pool.RentAsync(cancellationToken);
            
            return await result.Match(
                async lease =>
                {
                    await lease.DisposeAsync();
                    return HealthCheckResult.Healthy("Redis connection pool is healthy.");
                },
                error => Task.FromResult(HealthCheckResult.Unhealthy(
                    "Redis connection pool failed to acquire connection.",
                    data: new Dictionary<string, object>
                    {
                        ["error"] = error.Message
                    })));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Health check cancelled.");
        }
        catch (TimeoutException ex)
        {
            return HealthCheckResult.Degraded(
                "Redis connection pool timeout.",
                exception: ex,
                data: new Dictionary<string, object> { ["reason"] = "pool_timeout" });
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                "Redis is unavailable.",
                exception: ex,
                data: new Dictionary<string, object>
                {
                    ["reason"] = "connection_failed",
                    ["error_type"] = ex.GetType().Name
                });
        }
    }
}
