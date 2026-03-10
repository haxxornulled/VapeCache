namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Convenience composition for enabling the full Aspire integration surface in one fluent call.
/// </summary>
public static class AspireKitchenSinkExtensions
{
    /// <summary>
    /// Enables a full "kitchen sink" Aspire integration profile.
    /// </summary>
    /// <remarks>
    /// This composes:
    /// Redis service discovery, transport profile, stampede profile, health checks, telemetry,
    /// startup warmup, output caching integration, failover affinity hints, and endpoint auto-mapping.
    /// redis_exporter ingestion is optional and disabled by default.
    /// </remarks>
    public static AspireVapeCacheBuilder WithKitchenSink(
        this AspireVapeCacheBuilder builder,
        Action<VapeCacheKitchenSinkOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new VapeCacheKitchenSinkOptions();
        configure?.Invoke(options);

        ArgumentException.ThrowIfNullOrWhiteSpace(options.RedisConnectionName);

        builder.WithRedisFromAspire(options.RedisConnectionName)
            .UseTransport(options.TransportMode)
            .WithCacheStampedeProfile(options.StampedeProfile, options.ConfigureStampede);

        if (options.EnableHealthChecks)
            builder.WithHealthChecks();

        if (options.EnableAspireTelemetry)
            builder.WithAspireTelemetry(options.ConfigureTelemetry);

        if (options.EnableStartupWarmup)
            builder.WithStartupWarmup(options.ConfigureStartupWarmup);

        if (options.EnableAspNetCoreOutputCaching)
            builder.WithAspNetCoreOutputCaching(options.ConfigureOutputCache, options.ConfigureOutputCacheStore);

        if (options.EnableFailoverAffinityHints)
            builder.WithFailoverAffinityHints(options.ConfigureFailoverAffinityHints);

        if (options.EnableAutoMappedEndpoints)
            builder.WithAutoMappedEndpoints(options.ConfigureEndpoints);

        if (options.EnableRedisExporterMetrics)
            builder.WithRedisExporterMetrics(options.ConfigureRedisExporterMetrics, options.RedisExporterConfigurationSectionName);

        return builder;
    }
}
