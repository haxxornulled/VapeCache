namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Runtime options for scraping redis_exporter and projecting Redis server metrics into OpenTelemetry.
/// </summary>
public sealed class RedisExporterMetricsOptions
{
    public const string ConfigurationSectionName = "VapeCache:RedisExporter";
    public const string DefaultEndpoint = "http://localhost:9121/metrics";
    public static readonly TimeSpan MinimumPollInterval = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MinimumRequestTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Enables redis_exporter ingestion.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Full redis_exporter metrics endpoint URI.
    /// </summary>
    public string Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>
    /// Polling cadence for exporter scrapes.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Per-request timeout for exporter scrapes.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(2);
}
