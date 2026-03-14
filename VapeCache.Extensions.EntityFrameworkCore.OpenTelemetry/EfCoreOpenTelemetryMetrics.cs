using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry;

/// <summary>
/// EF Core cache observer telemetry instruments.
/// </summary>
public static class EfCoreOpenTelemetryMetrics
{
    /// <summary>
    /// Meter name for EF Core cache observer telemetry.
    /// </summary>
    public const string MeterName = "VapeCache.EFCore.Cache";

    /// <summary>
    /// Activity source name for EF Core cache observer telemetry.
    /// </summary>
    public const string ActivitySourceName = "VapeCache.EFCore.Cache";

    /// <summary>
    /// EF Core cache meter.
    /// </summary>
    public static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// EF Core cache activity source.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static readonly Counter<long> QueryKeyBuilt = Meter.CreateCounter<long>(
        "efcore.cache.query.key_built");

    public static readonly Counter<long> QueryExecutionCompleted = Meter.CreateCounter<long>(
        "efcore.cache.query.execution.completed");

    public static readonly Counter<long> QueryExecutionFailed = Meter.CreateCounter<long>(
        "efcore.cache.query.execution.failed");

    public static readonly Histogram<double> QueryExecutionMs = Meter.CreateHistogram<double>(
        "efcore.cache.query.execution.ms",
        unit: "ms");

    public static readonly Counter<long> InvalidationPlanCaptured = Meter.CreateCounter<long>(
        "efcore.cache.invalidation.plan.captured");

    public static readonly Histogram<long> InvalidationPlanZoneCount = Meter.CreateHistogram<long>(
        "efcore.cache.invalidation.plan.zone_count",
        unit: "zones");

    public static readonly Counter<long> ZoneInvalidated = Meter.CreateCounter<long>(
        "efcore.cache.invalidation.zone.invalidated");

    public static readonly Counter<long> ZoneInvalidationFailed = Meter.CreateCounter<long>(
        "efcore.cache.invalidation.zone.failed");
}
