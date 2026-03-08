using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using VapeCache.Extensions.Aspire.Hosting;

namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Enables redis_exporter ingestion so Redis server metrics from all clients flow into Aspire OTEL pipeline.
/// </summary>
public static class AspireRedisExporterMetricsExtensions
{
    /// <summary>
    /// Executes with redis exporter metrics.
    /// </summary>
    public static AspireVapeCacheBuilder WithRedisExporterMetrics(
        this AspireVapeCacheBuilder builder,
        Action<RedisExporterMetricsOptions>? configure = null,
        string configurationSectionName = RedisExporterMetricsOptions.ConfigurationSectionName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (string.IsNullOrWhiteSpace(configurationSectionName))
            throw new ArgumentException("Configuration section name is required.", nameof(configurationSectionName));

        var optionsBuilder = builder.Builder.Services
            .AddOptions<RedisExporterMetricsOptions>()
            .Bind(builder.Builder.Configuration.GetSection(configurationSectionName));

        optionsBuilder.PostConfigure(static options =>
        {
            if (string.IsNullOrWhiteSpace(options.Endpoint))
                options.Endpoint = RedisExporterMetricsOptions.DefaultEndpoint;

            if (options.PollInterval < RedisExporterMetricsOptions.MinimumPollInterval)
                options.PollInterval = RedisExporterMetricsOptions.MinimumPollInterval;

            if (options.RequestTimeout < RedisExporterMetricsOptions.MinimumRequestTimeout)
                options.RequestTimeout = RedisExporterMetricsOptions.MinimumRequestTimeout;
        });

        if (configure is not null)
            builder.Builder.Services.PostConfigure(configure);

        RedisExporterTelemetry.EnsureInitialized();

        builder.Builder.Services.AddHttpClient(RedisExporterMetricsHostedService.HttpClientName);
        builder.Builder.Services.TryAddSingleton<RedisExporterMetricsState>(static _ =>
        {
            var state = new RedisExporterMetricsState();
            RedisExporterTelemetry.Initialize(state);
            return state;
        });

        builder.Builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, RedisExporterMetricsHostedService>());

        return builder;
    }
}
