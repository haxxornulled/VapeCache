using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Telemetry options for VapeCache OpenTelemetry registration.
/// </summary>
public sealed class VapeCacheTelemetryOptions
{
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
    /// Optional callback to further configure metrics provider builder.
    /// </summary>
    public Action<MeterProviderBuilder>? ConfigureMetrics { get; set; }

    /// <summary>
    /// Optional callback to further configure tracing provider builder.
    /// </summary>
    public Action<TracerProviderBuilder>? ConfigureTracing { get; set; }
}
