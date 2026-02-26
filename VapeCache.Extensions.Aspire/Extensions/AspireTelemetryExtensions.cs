using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System.Text;

namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Extension methods for configuring VapeCache telemetry with .NET Aspire Dashboard.
/// </summary>
public static class AspireTelemetryExtensions
{
    private const string SeqDefaultOtlpBaseEndpoint = "http://localhost:5341/ingest/otlp";

    /// <summary>
    /// Configures OpenTelemetry to send VapeCache metrics and traces to Aspire Dashboard.
    /// Exposes cache hit/miss rates, latency, connection pool metrics, and distributed traces.
    /// </summary>
    /// <param name="builder">The VapeCache builder.</param>
    /// <param name="configure">Optional telemetry configuration callback for custom wrappers/exporters.</param>
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
    ///
    /// // Advanced/custom wrapper scenario:
    /// builder.AddVapeCache()
    ///     .WithAspireTelemetry(options =>
    ///     {
    ///         options.UseSeq(apiKey: "dev-seq-api-key")
    ///                .AddMetricsConfiguration(m => { /* add custom metric exporters */ })
    ///                .AddTracingConfiguration(t => { /* add custom trace exporters */ });
    ///     });
    /// </code>
    /// </example>
    public static AspireVapeCacheBuilder WithAspireTelemetry(
        this AspireVapeCacheBuilder builder,
        Action<VapeCacheTelemetryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var telemetryOptions = new VapeCacheTelemetryOptions();
        configure?.Invoke(telemetryOptions);

        var exporter = ResolveExporterConfiguration(builder.Builder.Configuration, telemetryOptions);

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

                if (exporter is not null)
                {
                    metrics.AddOtlpExporter(otlp =>
                    {
                        otlp.Protocol = exporter.Protocol;
                        otlp.Endpoint = exporter.MetricsEndpoint;
                        if (!string.IsNullOrWhiteSpace(exporter.Headers))
                            otlp.Headers = exporter.Headers;
                    });
                }

                telemetryOptions.ConfigureMetrics?.Invoke(metrics);
            })
            .WithTracing(tracing =>
            {
                // Register VapeCache activity sources for distributed tracing
                // This enables end-to-end trace visualization in Aspire Dashboard
                tracing.AddSource("VapeCache.Redis");

                if (exporter is not null)
                {
                    tracing.AddOtlpExporter(otlp =>
                    {
                        otlp.Protocol = exporter.Protocol;
                        otlp.Endpoint = exporter.TracesEndpoint;
                        if (!string.IsNullOrWhiteSpace(exporter.Headers))
                            otlp.Headers = exporter.Headers;
                    });
                }

                telemetryOptions.ConfigureTracing?.Invoke(tracing);
            });

        return builder;
    }

    private static ExporterConfiguration? ResolveExporterConfiguration(
        IConfiguration configuration,
        VapeCacheTelemetryOptions options)
    {
        if (!options.EnableOtlpExporter)
            return null;

        var endpointText = options.OtlpEndpoint;

        if (string.IsNullOrWhiteSpace(endpointText))
            endpointText = configuration["OpenTelemetry:Otlp:Endpoint"];

        if (string.IsNullOrWhiteSpace(endpointText))
            endpointText = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

        if (string.IsNullOrWhiteSpace(endpointText))
            endpointText = Environment.GetEnvironmentVariable("DOTNET_DASHBOARD_OTLP_ENDPOINT_URL");

        if (string.IsNullOrWhiteSpace(endpointText) && options.UseSeqAsDefaultExporter)
            endpointText = SeqDefaultOtlpBaseEndpoint;

        if (string.IsNullOrWhiteSpace(endpointText))
            return null;

        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpointUri))
            throw new ArgumentException($"Invalid OpenTelemetry OTLP endpoint: {endpointText}", nameof(options));

        var protocol = options.OtlpProtocol ?? InferProtocol(endpointUri);
        var metricsEndpoint = ResolveSignalEndpoint(endpointUri, protocol, signal: "metrics");
        var tracesEndpoint = ResolveSignalEndpoint(endpointUri, protocol, signal: "traces");
        var headers = ResolveOtlpHeaders(configuration, options);

        return new ExporterConfiguration(protocol, metricsEndpoint, tracesEndpoint, headers);
    }

    private static string? ResolveOtlpHeaders(IConfiguration configuration, VapeCacheTelemetryOptions options)
    {
        if (options.OtlpHeaders.Count > 0)
            return BuildOtlpHeaders(options.OtlpHeaders);

        var headerText = configuration["OpenTelemetry:Otlp:Headers"];
        if (string.IsNullOrWhiteSpace(headerText))
            headerText = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS");

        return string.IsNullOrWhiteSpace(headerText) ? null : headerText;
    }

    private static string BuildOtlpHeaders(IDictionary<string, string> headers)
    {
        var sb = new StringBuilder(headers.Count * 16);
        var first = true;

        foreach (var kvp in headers)
        {
            if (!first)
                sb.Append(',');
            first = false;

            sb.Append(kvp.Key.Trim());
            sb.Append('=');
            sb.Append(EscapeHeaderValue(kvp.Value.Trim()));
        }

        return sb.ToString();
    }

    private static string EscapeHeaderValue(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace(",", "\\,", StringComparison.Ordinal)
                .Replace("=", "\\=", StringComparison.Ordinal);

    private static OtlpExportProtocol InferProtocol(Uri endpoint)
    {
        if (endpoint.Port == 5341)
            return OtlpExportProtocol.HttpProtobuf;

        if (endpoint.Port == 4318)
            return OtlpExportProtocol.HttpProtobuf;

        if (endpoint.AbsolutePath.Contains("/ingest/otlp", StringComparison.OrdinalIgnoreCase))
            return OtlpExportProtocol.HttpProtobuf;

        if (endpoint.AbsolutePath.Contains("/v1/", StringComparison.OrdinalIgnoreCase))
            return OtlpExportProtocol.HttpProtobuf;

        return OtlpExportProtocol.Grpc;
    }

    private static Uri ResolveSignalEndpoint(Uri endpoint, OtlpExportProtocol protocol, string signal)
    {
        if (protocol == OtlpExportProtocol.Grpc)
            return endpoint;

        var endpointText = endpoint.ToString().TrimEnd('/');
        var signalSuffix = $"/v1/{signal}";

        if (endpointText.EndsWith(signalSuffix, StringComparison.OrdinalIgnoreCase))
            return endpoint;

        if (endpointText.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return new Uri($"{endpointText}/{signal}", UriKind.Absolute);

        if (endpointText.EndsWith("/ingest/otlp", StringComparison.OrdinalIgnoreCase))
            return new Uri($"{endpointText}{signalSuffix}", UriKind.Absolute);

        if (endpointText.Contains("/v1/", StringComparison.OrdinalIgnoreCase))
            return endpoint;

        return new Uri($"{endpointText}{signalSuffix}", UriKind.Absolute);
    }

    private sealed record ExporterConfiguration(
        OtlpExportProtocol Protocol,
        Uri MetricsEndpoint,
        Uri TracesEndpoint,
        string? Headers);
}
