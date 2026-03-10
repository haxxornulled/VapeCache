namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Production observability composition options for Aspire-hosted VapeCache services.
/// </summary>
public sealed class VapeCacheProductionObservabilityOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether VapeCache/Redis health checks are enabled.
    /// </summary>
    public bool EnableHealthChecks { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether OpenTelemetry exporter wiring is enabled.
    /// </summary>
    public bool EnableAspireTelemetry { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether startup warmup is enabled.
    /// </summary>
    public bool EnableStartupWarmup { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether redis_exporter ingestion is enabled.
    /// </summary>
    public bool EnableRedisExporterMetrics { get; set; }

    /// <summary>
    /// Gets or sets optional telemetry configuration.
    /// </summary>
    public Action<VapeCacheTelemetryOptions>? ConfigureTelemetry { get; set; }

    /// <summary>
    /// Gets or sets optional startup warmup configuration.
    /// </summary>
    public Action<VapeCacheStartupWarmupOptions>? ConfigureStartupWarmup { get; set; }

    /// <summary>
    /// Gets or sets optional redis_exporter metrics ingestion configuration.
    /// </summary>
    public Action<RedisExporterMetricsOptions>? ConfigureRedisExporterMetrics { get; set; }

    /// <summary>
    /// Gets or sets the configuration section used for redis_exporter options.
    /// </summary>
    public string RedisExporterConfigurationSectionName { get; set; } = RedisExporterMetricsOptions.ConfigurationSectionName;
}
