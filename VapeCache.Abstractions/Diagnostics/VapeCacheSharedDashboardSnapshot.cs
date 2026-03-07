using System.Text.Json.Serialization;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Abstractions.Diagnostics;

public static class VapeCacheSharedDashboardSnapshotStore
{
    public const string RedisKey = "vapecache:dashboard:shared:v1";
    public static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxSnapshotAge = TimeSpan.FromSeconds(10);
}

public sealed record VapeCacheSharedDashboardSnapshot(
    DateTimeOffset TimestampUtc,
    [property: JsonConverter(typeof(JsonStringEnumConverter<BackendType>))] BackendType Backend,
    double HitRate,
    long Reads,
    long Writes,
    long Hits,
    long Misses,
    long FallbackToMemory,
    long RedisBreakerOpened,
    long StampedeKeyRejected,
    long StampedeLockWaitTimeout,
    long StampedeFailureBackoffRejected,
    bool BreakerEnabled,
    bool BreakerOpen,
    int BreakerConsecutiveFailures,
    TimeSpan? BreakerOpenRemaining,
    bool BreakerForcedOpen,
    string? BreakerReason,
    RedisAutoscalerSnapshot? Autoscaler,
    IReadOnlyList<RedisMuxLaneSnapshot> Lanes,
    SpillStoreDiagnosticsSnapshot? Spill);
