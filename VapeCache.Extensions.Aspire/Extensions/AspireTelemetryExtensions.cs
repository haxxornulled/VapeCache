using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Extension methods for configuring VapeCache telemetry with .NET Aspire Dashboard.
/// </summary>
public static class AspireTelemetryExtensions
{
    /// <summary>
    /// Configures OpenTelemetry to send VapeCache metrics and traces to Aspire Dashboard.
    /// Exposes cache hit/miss rates, latency, connection pool metrics, and distributed traces.
    /// </summary>
    /// <param name="builder">The VapeCache builder.</param>
    /// <returns>The builder for method chaining.</returns>
    /// <remarks>
    /// <para>
    /// Aspire automatically configures the OTLP endpoint via environment variables:
    /// - OTEL_EXPORTER_OTLP_ENDPOINT (set by Aspire AppHost)
    /// - DOTNET_DASHBOARD_OTLP_ENDPOINT_URL (fallback)
    /// </para>
    /// <para>
    /// This extension registers VapeCache's OpenTelemetry meters and activity sources
    /// so metrics/traces flow to the Aspire Dashboard automatically.
    /// </para>
    /// <para>
    /// <strong>Meters registered:</strong>
    /// - VapeCache.Cache (cache hit/miss, operations)
    /// - VapeCache.Redis (Redis commands, connection pool)
    /// </para>
    /// <para>
    /// <strong>Activity sources registered:</strong>
    /// - VapeCache.Redis (distributed tracing for Redis operations)
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.AddVapeCache()
    ///     .WithRedisFromAspire("redis")
    ///     .WithAspireTelemetry();  // Metrics visible in Aspire Dashboard
    /// </code>
    /// </example>
    public static AspireVapeCacheBuilder WithAspireTelemetry(
        this AspireVapeCacheBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                // Register VapeCache meters
                // These are already defined in VapeCache.Infrastructure:
                // - CacheTelemetry.Meter ("VapeCache.Cache")
                // - RedisTelemetry.Meter ("VapeCache.Redis")
                metrics.AddMeter("VapeCache.Cache");   // Cache hit/miss, operations
                metrics.AddMeter("VapeCache.Redis");   // Redis commands, pool metrics

                // Configure histogram buckets for cache operation latency
                // Optimized for sub-millisecond to multi-second operations
                metrics.AddView(
                    instrumentName: "cache.op.ms",
                    new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = new double[] { 0.1, 0.5, 1.0, 2.5, 5.0, 10.0, 25.0, 50.0, 100.0, 250.0, 500.0, 1000.0 }
                    });

                // Configure histogram buckets for Redis command latency
                metrics.AddView(
                    instrumentName: "redis.cmd.ms",
                    new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = new double[] { 0.1, 0.5, 1.0, 2.5, 5.0, 10.0, 25.0, 50.0, 100.0, 250.0, 500.0, 1000.0 }
                    });

                // Configure histogram buckets for pool wait time
                metrics.AddView(
                    instrumentName: "redis.pool.wait.ms",
                    new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = new double[] { 0.1, 0.5, 1.0, 2.5, 5.0, 10.0, 25.0, 50.0, 100.0, 250.0, 500.0, 1000.0 }
                    });
            })
            .WithTracing(tracing =>
            {
                // Register VapeCache activity sources for distributed tracing
                // This enables end-to-end trace visualization in Aspire Dashboard
                tracing.AddSource("VapeCache.Redis");
            });

        return builder;
    }
}
