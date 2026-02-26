using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Telemetry options for VapeCache OpenTelemetry registration.
/// </summary>
public sealed class VapeCacheTelemetryOptions
{
    private const string SeqDefaultOtlpBaseEndpoint = "http://localhost:5341/ingest/otlp";
    private const string SeqApiKeyHeaderName = "X-Seq-ApiKey";

    /// <summary>
    /// Whether to register OTLP exporters for metrics and traces.
    /// </summary>
    public bool EnableOtlpExporter { get; set; } = true;

    /// <summary>
    /// When no endpoint is configured via options/config/env, use Seq OTLP endpoint fallback.
    /// </summary>
    public bool UseSeqAsDefaultExporter { get; set; } = true;

    /// <summary>
    /// Optional explicit OTLP endpoint. Can be base endpoint or signal-specific endpoint.
    /// </summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>
    /// Optional explicit OTLP protocol. If null, protocol is inferred from endpoint.
    /// </summary>
    public OtlpExportProtocol? OtlpProtocol { get; set; }

    /// <summary>
    /// Optional OTLP headers forwarded to exporter configuration (e.g. auth headers).
    /// </summary>
    public IDictionary<string, string> OtlpHeaders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional callback to further configure metrics provider builder.
    /// </summary>
    public Action<MeterProviderBuilder>? ConfigureMetrics { get; set; }

    /// <summary>
    /// Optional callback to further configure tracing provider builder.
    /// </summary>
    public Action<TracerProviderBuilder>? ConfigureTracing { get; set; }

    /// <summary>
    /// Enables OTLP export to Seq and configures HttpProtobuf protocol.
    /// </summary>
    /// <param name="seqBaseUrl">
    /// Seq base URL (for example: http://localhost:5341) or full OTLP URL.
    /// If omitted, uses the default local Seq endpoint.
    /// </param>
    /// <param name="apiKey">Optional Seq API key. Sent as X-Seq-ApiKey OTLP header.</param>
    /// <returns>The same options instance for fluent chaining.</returns>
    public VapeCacheTelemetryOptions UseSeq(string? seqBaseUrl = null, string? apiKey = null)
    {
        EnableOtlpExporter = true;
        UseSeqAsDefaultExporter = true;
        OtlpProtocol = OtlpExportProtocol.HttpProtobuf;
        OtlpEndpoint = NormalizeSeqEndpoint(seqBaseUrl);

        if (!string.IsNullOrWhiteSpace(apiKey))
            OtlpHeaders[SeqApiKeyHeaderName] = apiKey.Trim();

        return this;
    }

    /// <summary>
    /// Enables OTLP export to a custom endpoint.
    /// </summary>
    /// <param name="endpoint">OTLP endpoint URL.</param>
    /// <param name="protocol">Optional protocol override. If null, protocol is inferred.</param>
    /// <returns>The same options instance for fluent chaining.</returns>
    public VapeCacheTelemetryOptions UseOtlp(string endpoint, OtlpExportProtocol? protocol = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("OTLP endpoint is required.", nameof(endpoint));

        EnableOtlpExporter = true;
        OtlpEndpoint = endpoint.Trim();
        OtlpProtocol = protocol;
        return this;
    }

    /// <summary>
    /// Disables OTLP exporter registration.
    /// </summary>
    /// <returns>The same options instance for fluent chaining.</returns>
    public VapeCacheTelemetryOptions DisableOtlpExporter()
    {
        EnableOtlpExporter = false;
        return this;
    }

    /// <summary>
    /// Adds or replaces an OTLP header.
    /// </summary>
    /// <param name="name">Header name.</param>
    /// <param name="value">Header value.</param>
    /// <returns>The same options instance for fluent chaining.</returns>
    public VapeCacheTelemetryOptions WithOtlpHeader(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Header name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Header value is required.", nameof(value));

        OtlpHeaders[name.Trim()] = value.Trim();
        return this;
    }

    /// <summary>
    /// Appends a metrics builder callback while preserving existing callbacks.
    /// </summary>
    /// <param name="configure">Metrics builder callback.</param>
    /// <returns>The same options instance for fluent chaining.</returns>
    public VapeCacheTelemetryOptions AddMetricsConfiguration(Action<MeterProviderBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ConfigureMetrics += configure;
        return this;
    }

    /// <summary>
    /// Appends a tracing builder callback while preserving existing callbacks.
    /// </summary>
    /// <param name="configure">Tracing builder callback.</param>
    /// <returns>The same options instance for fluent chaining.</returns>
    public VapeCacheTelemetryOptions AddTracingConfiguration(Action<TracerProviderBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ConfigureTracing += configure;
        return this;
    }

    private static string NormalizeSeqEndpoint(string? seqBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(seqBaseUrl))
            return SeqDefaultOtlpBaseEndpoint;

        var trimmed = seqBaseUrl.Trim().TrimEnd('/');
        if (trimmed.Contains("/ingest/otlp", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        return $"{trimmed}/ingest/otlp";
    }
}
