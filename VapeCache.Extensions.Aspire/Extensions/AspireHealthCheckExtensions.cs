using Microsoft.Extensions.DependencyInjection;
using VapeCache.Extensions.Aspire.HealthChecks;

namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Extension methods for adding VapeCache health checks to Aspire applications.
/// </summary>
public static class AspireHealthCheckExtensions
{
    /// <summary>
    /// Adds VapeCache and Redis health checks to the application.
    /// Endpoint mapping is handled by the host (Kubernetes, Azure Container Apps, etc.).
    /// </summary>
    /// <param name="builder">The VapeCache builder.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <remarks>
    /// <para>
    /// This extension registers two health checks:
    /// - "redis": Acquires a pooled connection and performs a Redis PING
    /// - "vapecache": Executes a cache read and reports breaker/fallback state
    /// </para>
    /// <para>
    /// <strong>Health Check Results:</strong>
    /// - Healthy: Redis is operational, circuit breaker closed
    /// - Degraded: Circuit breaker open (using in-memory fallback) OR pool under pressure
    /// - Unhealthy: Redis connection failed
    /// </para>
    /// <para>
    /// <strong>Usage in your host:</strong>
    /// </para>
    /// <code>
    /// app.MapHealthChecks("/health");                     // All health checks
    /// app.MapHealthChecks("/health/redis", new HealthCheckOptions
    /// {
    ///     Predicate = check => check.Name == "redis"
    /// });
    /// </code>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.AddVapeCache()
    ///     .WithRedisFromAspire("redis")
    ///     .WithHealthChecks();  // Adds health checks
    ///
    /// var app = builder.Build();
    /// app.MapHealthChecks("/health");
    /// app.Run();
    /// </code>
    /// </example>
    public static AspireVapeCacheBuilder WithHealthChecks(
        this AspireVapeCacheBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Builder.Services.AddHealthChecks()
            .AddCheck<RedisHealthCheck>(
                name: "redis",
                failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
                tags: new[] { "vapecache", "redis", "infrastructure" })
            .AddCheck<VapeCacheStartupReadinessHealthCheck>(
                name: "vapecache-startup-readiness",
                failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
                tags: new[] { "vapecache", "startup", "readiness" })
            .AddCheck<VapeCacheHealthCheck>(
                name: "vapecache",
                failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
                tags: new[] { "vapecache", "cache" });

        return builder;
    }
}
