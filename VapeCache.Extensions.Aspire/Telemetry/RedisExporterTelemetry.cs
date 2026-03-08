using System.Diagnostics.Metrics;

namespace VapeCache.Extensions.Aspire;

internal static class RedisExporterTelemetry
{
    private static RedisExporterMetricsState? _state;

    public static readonly Meter Meter = new("VapeCache.RedisServer");

    public static readonly ObservableGauge<int> ExporterEnabled = Meter.CreateObservableGauge(
        name: "redis.exporter.enabled",
        observeValue: static () => Current.Enabled ? 1 : 0,
        description: "Whether redis_exporter ingestion is enabled");

    public static readonly ObservableGauge<int> ExporterUp = Meter.CreateObservableGauge(
        name: "redis.exporter.up",
        observeValue: static () => Current.Up,
        description: "redis_exporter scrape health (1=up,0=down)");

    public static readonly ObservableGauge<double> LastSuccessAgeSeconds = Meter.CreateObservableGauge(
        name: "redis.exporter.last_success.age.seconds",
        observeValue: static () => Current.LastSuccessAgeSeconds,
        unit: "s",
        description: "Seconds since the last successful redis_exporter scrape (-1 when none)");

    public static readonly ObservableGauge<double> ConnectedClients = Meter.CreateObservableGauge(
        name: "redis.server.connected_clients",
        observeValue: static () => Current.ConnectedClients,
        description: "Connected Redis clients from redis_exporter");

    public static readonly ObservableGauge<double> BlockedClients = Meter.CreateObservableGauge(
        name: "redis.server.blocked_clients",
        observeValue: static () => Current.BlockedClients,
        description: "Blocked Redis clients from redis_exporter");

    public static readonly ObservableGauge<double> OpsPerSecond = Meter.CreateObservableGauge(
        name: "redis.server.ops_per_sec",
        observeValue: static () => Current.OpsPerSecond,
        description: "Redis operations per second from redis_exporter");

    public static readonly ObservableGauge<double> UsedMemoryBytes = Meter.CreateObservableGauge(
        name: "redis.server.memory.used.bytes",
        observeValue: static () => Current.UsedMemoryBytes,
        unit: "bytes",
        description: "Redis used memory bytes from redis_exporter");

    public static readonly ObservableGauge<double> MaxMemoryBytes = Meter.CreateObservableGauge(
        name: "redis.server.memory.max.bytes",
        observeValue: static () => Current.MaxMemoryBytes,
        unit: "bytes",
        description: "Redis max memory bytes from redis_exporter");

    public static readonly ObservableGauge<double> CacheHitRatio = Meter.CreateObservableGauge(
        name: "redis.server.keyspace.hit_ratio",
        observeValue: static () => Current.CacheHitRatio,
        unit: "ratio",
        description: "Redis keyspace hit ratio derived from redis_exporter counters");

    public static readonly ObservableGauge<long> CommandsProcessedTotal = Meter.CreateObservableGauge(
        name: "redis.server.commands.processed.total",
        observeValue: static () => Current.CommandsProcessedTotal,
        description: "Current Redis commands processed total from redis_exporter");

    public static readonly ObservableGauge<long> KeyspaceHitsTotal = Meter.CreateObservableGauge(
        name: "redis.server.keyspace.hits.total",
        observeValue: static () => Current.KeyspaceHitsTotal,
        description: "Current Redis keyspace hits total from redis_exporter");

    public static readonly ObservableGauge<long> KeyspaceMissesTotal = Meter.CreateObservableGauge(
        name: "redis.server.keyspace.misses.total",
        observeValue: static () => Current.KeyspaceMissesTotal,
        description: "Current Redis keyspace misses total from redis_exporter");

    public static readonly ObservableGauge<long> EvictedKeysTotal = Meter.CreateObservableGauge(
        name: "redis.server.evicted_keys.total",
        observeValue: static () => Current.EvictedKeysTotal,
        description: "Current Redis evicted keys total from redis_exporter");

    public static readonly ObservableGauge<long> NetInputBytesTotal = Meter.CreateObservableGauge(
        name: "redis.server.net.input.bytes.total",
        observeValue: static () => Current.NetInputBytesTotal,
        unit: "bytes",
        description: "Current inbound Redis bytes total from redis_exporter");

    public static readonly ObservableGauge<long> NetOutputBytesTotal = Meter.CreateObservableGauge(
        name: "redis.server.net.output.bytes.total",
        observeValue: static () => Current.NetOutputBytesTotal,
        unit: "bytes",
        description: "Current outbound Redis bytes total from redis_exporter");

    private static RedisExporterMetricsSnapshot Current
        => _state?.Current ?? RedisExporterMetricsSnapshot.Unknown;

    internal static void Initialize(RedisExporterMetricsState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        EnsureInitialized();
    }

    internal static void EnsureInitialized()
    {
        _ = ExporterEnabled;
        _ = ExporterUp;
        _ = LastSuccessAgeSeconds;
        _ = ConnectedClients;
        _ = BlockedClients;
        _ = OpsPerSecond;
        _ = UsedMemoryBytes;
        _ = MaxMemoryBytes;
        _ = CacheHitRatio;
        _ = CommandsProcessedTotal;
        _ = KeyspaceHitsTotal;
        _ = KeyspaceMissesTotal;
        _ = EvictedKeysTotal;
        _ = NetInputBytesTotal;
        _ = NetOutputBytesTotal;
    }

    internal static void ResetForTesting()
    {
        _state = null;
    }
}
