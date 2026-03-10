using Microsoft.AspNetCore.OutputCaching;
using VapeCache.Abstractions.Caching;
using VapeCache.Extensions.AspNetCore;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Composite options for enabling the full Aspire integration surface in one call.
/// </summary>
public sealed class VapeCacheKitchenSinkOptions
{
    /// <summary>
    /// Gets or sets the Aspire connection string resource name.
    /// </summary>
    public string RedisConnectionName { get; set; } = "redis";

    /// <summary>
    /// Gets or sets the high-level transport mode.
    /// </summary>
    public VapeCacheAspireTransportMode TransportMode { get; set; } = VapeCacheAspireTransportMode.Balanced;

    /// <summary>
    /// Gets or sets the cache stampede profile.
    /// </summary>
    public CacheStampedeProfile StampedeProfile { get; set; } = CacheStampedeProfile.Balanced;

    /// <summary>
    /// Gets or sets a value indicating whether health checks are enabled.
    /// </summary>
    public bool EnableHealthChecks { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether OTLP telemetry wiring is enabled.
    /// </summary>
    public bool EnableAspireTelemetry { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether startup warmup is enabled.
    /// </summary>
    public bool EnableStartupWarmup { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether ASP.NET Core output caching integration is enabled.
    /// </summary>
    public bool EnableAspNetCoreOutputCaching { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether failover affinity hint options are registered.
    /// </summary>
    public bool EnableFailoverAffinityHints { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether endpoint auto-mapping is enabled.
    /// </summary>
    public bool EnableAutoMappedEndpoints { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether redis_exporter scrape ingestion is enabled.
    /// </summary>
    public bool EnableRedisExporterMetrics { get; set; }

    /// <summary>
    /// Gets or sets optional stampede profile overrides.
    /// </summary>
    public Action<CacheStampedeOptionsBuilder>? ConfigureStampede { get; set; }

    /// <summary>
    /// Gets or sets optional telemetry configuration.
    /// </summary>
    public Action<VapeCacheTelemetryOptions>? ConfigureTelemetry { get; set; }

    /// <summary>
    /// Gets or sets optional startup warmup configuration.
    /// </summary>
    public Action<VapeCacheStartupWarmupOptions>? ConfigureStartupWarmup { get; set; }

    /// <summary>
    /// Gets or sets optional ASP.NET Core output cache configuration.
    /// </summary>
    public Action<OutputCacheOptions>? ConfigureOutputCache { get; set; }

    /// <summary>
    /// Gets or sets optional output-cache store configuration.
    /// </summary>
    public Action<VapeCacheOutputCacheStoreOptions>? ConfigureOutputCacheStore { get; set; }

    /// <summary>
    /// Gets or sets optional failover affinity hint configuration.
    /// </summary>
    public Action<VapeCacheFailoverAffinityOptions>? ConfigureFailoverAffinityHints { get; set; }

    /// <summary>
    /// Gets or sets optional endpoint auto-mapping configuration.
    /// </summary>
    public Action<VapeCacheEndpointOptions>? ConfigureEndpoints { get; set; }

    /// <summary>
    /// Gets or sets optional redis_exporter metrics ingestion configuration.
    /// </summary>
    public Action<RedisExporterMetricsOptions>? ConfigureRedisExporterMetrics { get; set; }

    /// <summary>
    /// Gets or sets the configuration section name used for redis_exporter options.
    /// </summary>
    public string RedisExporterConfigurationSectionName { get; set; } = RedisExporterMetricsOptions.ConfigurationSectionName;
}
