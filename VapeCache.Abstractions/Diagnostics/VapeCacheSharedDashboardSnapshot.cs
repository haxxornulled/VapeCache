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
public sealed record VapeCacheSharedDashboardSnapshot
{
    public VapeCacheSharedDashboardSnapshot(
        DateTimeOffset TimestampUtc,
        BackendType Backend,
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
        SpillStoreDiagnosticsSnapshot? Spill)
    {
        this.TimestampUtc = TimestampUtc;
        this.Backend = Backend;
        this.HitRate = HitRate;
        this.Reads = Reads;
        this.Writes = Writes;
        this.Hits = Hits;
        this.Misses = Misses;
        this.FallbackToMemory = FallbackToMemory;
        this.RedisBreakerOpened = RedisBreakerOpened;
        this.StampedeKeyRejected = StampedeKeyRejected;
        this.StampedeLockWaitTimeout = StampedeLockWaitTimeout;
        this.StampedeFailureBackoffRejected = StampedeFailureBackoffRejected;
        this.BreakerEnabled = BreakerEnabled;
        this.BreakerOpen = BreakerOpen;
        this.BreakerConsecutiveFailures = BreakerConsecutiveFailures;
        this.BreakerOpenRemaining = BreakerOpenRemaining;
        this.BreakerForcedOpen = BreakerForcedOpen;
        this.BreakerReason = BreakerReason;
        this.Autoscaler = Autoscaler;
        this.Lanes = Lanes;
        this.Spill = Spill;
    }

    public DateTimeOffset TimestampUtc { get; init; }
    [property: JsonConverter(typeof(JsonStringEnumConverter<BackendType>))]
    public BackendType Backend { get; init; }
    public double HitRate { get; init; }
    public long Reads { get; init; }
    public long Writes { get; init; }
    public long Hits { get; init; }
    public long Misses { get; init; }
    public long FallbackToMemory { get; init; }
    public long RedisBreakerOpened { get; init; }
    public long StampedeKeyRejected { get; init; }
    public long StampedeLockWaitTimeout { get; init; }
    public long StampedeFailureBackoffRejected { get; init; }
    public bool BreakerEnabled { get; init; }
    public bool BreakerOpen { get; init; }
    public int BreakerConsecutiveFailures { get; init; }
    public TimeSpan? BreakerOpenRemaining { get; init; }
    public bool BreakerForcedOpen { get; init; }
    public string? BreakerReason { get; init; }
    public RedisAutoscalerSnapshot? Autoscaler { get; init; }
    public IReadOnlyList<RedisMuxLaneSnapshot> Lanes { get; init; }
    public SpillStoreDiagnosticsSnapshot? Spill { get; init; }
}
