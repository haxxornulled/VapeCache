namespace VapeCache.Extensions.Aspire;

internal readonly record struct RedisExporterMetricValues(
    int ExporterUp,
    double ConnectedClients,
    double BlockedClients,
    double OpsPerSecond,
    double UsedMemoryBytes,
    double MaxMemoryBytes,
    long CommandsProcessedTotal,
    long KeyspaceHitsTotal,
    long KeyspaceMissesTotal,
    long EvictedKeysTotal,
    long NetInputBytesTotal,
    long NetOutputBytesTotal);

internal sealed record RedisExporterMetricsSnapshot(
    bool Enabled,
    int Up,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? LastSuccessUtc,
    double ConnectedClients,
    double BlockedClients,
    double OpsPerSecond,
    double UsedMemoryBytes,
    double MaxMemoryBytes,
    long CommandsProcessedTotal,
    long KeyspaceHitsTotal,
    long KeyspaceMissesTotal,
    long EvictedKeysTotal,
    long NetInputBytesTotal,
    long NetOutputBytesTotal)
{
    public static RedisExporterMetricsSnapshot Unknown { get; } = new(
        Enabled: false,
        Up: 0,
        ObservedAtUtc: DateTimeOffset.UnixEpoch,
        LastSuccessUtc: null,
        ConnectedClients: 0d,
        BlockedClients: 0d,
        OpsPerSecond: 0d,
        UsedMemoryBytes: 0d,
        MaxMemoryBytes: 0d,
        CommandsProcessedTotal: 0L,
        KeyspaceHitsTotal: 0L,
        KeyspaceMissesTotal: 0L,
        EvictedKeysTotal: 0L,
        NetInputBytesTotal: 0L,
        NetOutputBytesTotal: 0L);

    public static RedisExporterMetricsSnapshot Disabled(DateTimeOffset observedAtUtc) => new(
        Enabled: false,
        Up: 0,
        ObservedAtUtc: observedAtUtc,
        LastSuccessUtc: null,
        ConnectedClients: 0d,
        BlockedClients: 0d,
        OpsPerSecond: 0d,
        UsedMemoryBytes: 0d,
        MaxMemoryBytes: 0d,
        CommandsProcessedTotal: 0L,
        KeyspaceHitsTotal: 0L,
        KeyspaceMissesTotal: 0L,
        EvictedKeysTotal: 0L,
        NetInputBytesTotal: 0L,
        NetOutputBytesTotal: 0L);

    public static RedisExporterMetricsSnapshot Failed(
        RedisExporterMetricsSnapshot previous,
        DateTimeOffset observedAtUtc) => previous with
        {
            Enabled = true,
            Up = 0,
            ObservedAtUtc = observedAtUtc
        };

    public static RedisExporterMetricsSnapshot Success(
        in RedisExporterMetricValues values,
        DateTimeOffset observedAtUtc) => new(
        Enabled: true,
        Up: values.ExporterUp > 0 ? 1 : 0,
        ObservedAtUtc: observedAtUtc,
        LastSuccessUtc: observedAtUtc,
        ConnectedClients: values.ConnectedClients,
        BlockedClients: values.BlockedClients,
        OpsPerSecond: values.OpsPerSecond,
        UsedMemoryBytes: values.UsedMemoryBytes,
        MaxMemoryBytes: values.MaxMemoryBytes,
        CommandsProcessedTotal: values.CommandsProcessedTotal,
        KeyspaceHitsTotal: values.KeyspaceHitsTotal,
        KeyspaceMissesTotal: values.KeyspaceMissesTotal,
        EvictedKeysTotal: values.EvictedKeysTotal,
        NetInputBytesTotal: values.NetInputBytesTotal,
        NetOutputBytesTotal: values.NetOutputBytesTotal);

    public double LastSuccessAgeSeconds
        => LastSuccessUtc is null
            ? -1d
            : Math.Max(0d, (ObservedAtUtc - LastSuccessUtc.Value).TotalSeconds);

    public double CacheHitRatio
    {
        get
        {
            var total = KeyspaceHitsTotal + KeyspaceMissesTotal;
            return total <= 0L
                ? 0d
                : (double)KeyspaceHitsTotal / total;
        }
    }
}
