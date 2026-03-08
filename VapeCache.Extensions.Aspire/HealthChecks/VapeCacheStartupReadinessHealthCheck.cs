using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace VapeCache.Extensions.Aspire.HealthChecks;

/// <summary>
/// Represents the vape cache startup readiness health check.
/// </summary>
public sealed class VapeCacheStartupReadinessHealthCheck(IVapeCacheStartupReadiness readiness) : IHealthCheck
{
    /// <summary>
    /// Executes check health async.
    /// </summary>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>
        {
            ["is_running"] = readiness.IsRunning,
            ["is_ready"] = readiness.IsReady,
            ["target_connections"] = readiness.TargetConnections,
            ["successful_connections"] = readiness.SuccessfulConnections,
            ["failed_connections"] = readiness.FailedConnections
        };

        if (readiness.CompletedAtUtc is { } completedAtUtc)
            data["completed_at_utc"] = completedAtUtc;

        if (!string.IsNullOrWhiteSpace(readiness.Status))
            data["status"] = readiness.Status!;

        if (readiness.IsReady)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "VapeCache startup warmup is ready.",
                data: data));
        }

        if (readiness.IsRunning)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "VapeCache startup warmup is still running.",
                data: data));
        }

        if (readiness.LastError is { } ex)
        {
            data["error_type"] = ex.GetType().Name;
            data["error_message"] = ex.Message;
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "VapeCache startup warmup failed.",
                exception: ex,
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Degraded(
            "VapeCache startup warmup has not reached readiness.",
            data: data));
    }
}

