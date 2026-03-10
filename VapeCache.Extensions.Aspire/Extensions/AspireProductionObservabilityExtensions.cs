namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Production-focused observability composition for Aspire-hosted VapeCache services.
/// </summary>
public static class AspireProductionObservabilityExtensions
{
    /// <summary>
    /// Enables production observability defaults without enabling app-hosted debug/admin route surfaces.
    /// </summary>
    /// <remarks>
    /// This composes health checks and OpenTelemetry wiring by default.
    /// Startup warmup and redis_exporter ingestion are optional.
    /// </remarks>
    public static AspireVapeCacheBuilder WithProductionObservability(
        this AspireVapeCacheBuilder builder,
        Action<VapeCacheProductionObservabilityOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new VapeCacheProductionObservabilityOptions();
        configure?.Invoke(options);

        if (options.EnableHealthChecks)
            builder.WithHealthChecks();

        if (options.EnableAspireTelemetry)
            builder.WithAspireTelemetry(options.ConfigureTelemetry);

        if (options.EnableStartupWarmup)
            builder.WithStartupWarmup(options.ConfigureStartupWarmup);

        if (options.EnableRedisExporterMetrics)
            builder.WithRedisExporterMetrics(options.ConfigureRedisExporterMetrics, options.RedisExporterConfigurationSectionName);

        return builder;
    }
}
