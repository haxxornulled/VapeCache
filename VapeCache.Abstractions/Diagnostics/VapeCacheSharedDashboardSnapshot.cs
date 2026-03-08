using System.Text.Json.Serialization;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Abstractions.Diagnostics;

/// <summary>
/// Represents the vape cache shared dashboard snapshot store.
/// </summary>
public static class VapeCacheSharedDashboardSnapshotStore
{
    /// <summary>
    /// Defines the redis key.
    /// </summary>
    public const string RedisKey = "vapecache:dashboard:shared:v1";
    /// <summary>
    /// Executes from seconds.
    /// </summary>
    public static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(5);
    /// <summary>
    /// Executes from seconds.
    /// </summary>
    public static readonly TimeSpan MaxSnapshotAge = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Represents the vape cache shared dashboard snapshot.
/// </summary>
/// <param name="TimestampUtc">Snapshot timestamp in UTC.</param>
/// <param name="Backend">Active backend used for the snapshot.</param>
/// <param name="HitRate">Cache hit rate value in the current window.</param>
/// <param name="Reads">Total read operations.</param>
/// <param name="Writes">Total write operations.</param>
/// <param name="Hits">Total cache hits.</param>
/// <param name="Misses">Total cache misses.</param>
/// <param name="FallbackToMemory">Total operations served from in-memory fallback.</param>
/// <param name="RedisBreakerOpened">Total times the Redis breaker opened.</param>
/// <param name="StampedeKeyRejected">Total requests rejected due to stampede key pressure.</param>
/// <param name="StampedeLockWaitTimeout">Total requests that timed out waiting for stampede locks.</param>
/// <param name="StampedeFailureBackoffRejected">Total requests rejected due to failure-backoff state.</param>
/// <param name="BreakerEnabled">Whether the breaker is enabled.</param>
/// <param name="BreakerOpen">Whether the breaker is currently open.</param>
/// <param name="BreakerConsecutiveFailures">Current consecutive breaker failure count.</param>
/// <param name="BreakerOpenRemaining">Remaining open interval for the breaker, when available.</param>
/// <param name="BreakerForcedOpen">Whether the breaker was manually forced open.</param>
/// <param name="BreakerReason">Optional breaker reason text.</param>
/// <param name="Autoscaler">Current autoscaler snapshot.</param>
/// <param name="Lanes">Current lane snapshots.</param>
/// <param name="Spill">Current spill store diagnostics.</param>
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
