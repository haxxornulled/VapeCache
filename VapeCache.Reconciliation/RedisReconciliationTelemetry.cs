using System.Diagnostics.Metrics;

namespace VapeCache.Reconciliation;

internal static class RedisReconciliationTelemetry
{
    private static readonly Meter Meter = new("VapeCache.Reconciliation", "1.0.0");

    public static readonly Counter<long> Runs = Meter.CreateCounter<long>("vapecache.reconciliation.runs");
    public static readonly Counter<long> Tracked = Meter.CreateCounter<long>("vapecache.reconciliation.tracked");
    public static readonly Counter<long> Dropped = Meter.CreateCounter<long>("vapecache.reconciliation.dropped");
    public static readonly Counter<long> Synced = Meter.CreateCounter<long>("vapecache.reconciliation.synced");
    public static readonly Counter<long> Skipped = Meter.CreateCounter<long>("vapecache.reconciliation.skipped");
    public static readonly Counter<long> Failed = Meter.CreateCounter<long>("vapecache.reconciliation.failed");
    public static readonly Histogram<double> RunMs = Meter.CreateHistogram<double>("vapecache.reconciliation.run_ms", unit: "ms");
}
