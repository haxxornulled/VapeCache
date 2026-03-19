using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Diagnostics;

namespace VapeCache.UI.Features.Admin;

/// <summary>
/// Provides runtime stats snapshots for the admin UI.
/// </summary>
public interface IVapeCacheAdminStatsSnapshotProvider
{
    /// <summary>
    /// Gets a point-in-time stats snapshot for UI projection.
    /// </summary>
    VapeCacheAdminStatsSnapshot GetSnapshot();
}

/// <summary>
/// Provides invalidation operations used by admin controls.
/// </summary>
public interface IVapeCacheAdminInvalidationOperationsFacade
{
    /// <summary>
    /// Invalidates the specified tag.
    /// </summary>
    ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct = default);

    /// <summary>
    /// Invalidates the specified zone.
    /// </summary>
    ValueTask<long> InvalidateZoneAsync(string zone, CancellationToken ct = default);

    /// <summary>
    /// Invalidates a specific cache key.
    /// </summary>
    ValueTask<bool> InvalidateKeyAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// Provides autoscaler status for the admin UI.
/// </summary>
public interface IVapeCacheAdminAutoscalerStatusProvider
{
    /// <summary>
    /// Gets autoscaler status when available in the current runtime profile.
    /// </summary>
    RedisAutoscalerSnapshot? GetStatus();
}

/// <summary>
/// Provides spill diagnostics status for the admin UI.
/// </summary>
public interface IVapeCacheAdminSpillDiagnosticsProvider
{
    /// <summary>
    /// Gets spill diagnostics when available in the current runtime profile.
    /// </summary>
    SpillStoreDiagnosticsSnapshot? GetStatus();
}

/// <summary>
/// Provides reconciliation state and operations for admin controls.
/// </summary>
public interface IVapeCacheAdminReconciliationStatusProvider
{
    /// <summary>
    /// Gets current reconciliation status.
    /// </summary>
    VapeCacheAdminReconciliationStatus GetStatus();

    /// <summary>
    /// Runs a reconciliation pass if available.
    /// </summary>
    ValueTask<bool> ReconcileAsync(CancellationToken ct = default);

    /// <summary>
    /// Flushes persisted reconciliation state if available.
    /// </summary>
    ValueTask<bool> FlushAsync(CancellationToken ct = default);
}

/// <summary>
/// Provides breaker state for admin status pages.
/// </summary>
public interface IVapeCacheAdminBreakerStatusProvider
{
    /// <summary>
    /// Gets the current breaker state.
    /// </summary>
    VapeCacheAdminBreakerStatus GetStatus();
}

/// <summary>
/// Provides cache intent/policy inspection data for admin pages.
/// </summary>
public interface IVapeCacheAdminPolicyInspectionProvider
{
    /// <summary>
    /// Gets recent policy entries.
    /// </summary>
    IReadOnlyList<CacheIntentEntry> GetRecent(int take = 100);
}

/// <summary>
/// Provides event/stream feed availability and endpoint status.
/// </summary>
public interface IVapeCacheAdminEventStreamFeedProvider
{
    /// <summary>
    /// Gets current stream/feed status.
    /// </summary>
    VapeCacheAdminEventStreamStatus GetStatus();
}

/// <summary>
/// Runtime stats snapshot projected for admin UI composition.
/// </summary>
public sealed record VapeCacheAdminStatsSnapshot(
    DateTimeOffset TimestampUtc,
    BackendType Backend,
    CacheStatsSnapshot Stats,
    long Reads,
    long Writes,
    double HitRate,
    IReadOnlyList<RedisMuxLaneSnapshot> Lanes,
    int HealthyLaneCount);

/// <summary>
/// Runtime breaker status projected for admin UI composition.
/// </summary>
public sealed record VapeCacheAdminBreakerStatus(
    bool Enabled,
    bool IsOpen,
    int ConsecutiveFailures,
    TimeSpan? OpenRemaining,
    bool HalfOpenProbeInFlight,
    bool IsForcedOpen,
    string? Reason);

/// <summary>
/// Runtime reconciliation status projected for admin UI composition.
/// </summary>
public sealed record VapeCacheAdminReconciliationStatus(
    bool Enabled,
    int PendingOperations);

/// <summary>
/// Runtime event-stream status projected for admin UI composition.
/// </summary>
public sealed record VapeCacheAdminEventStreamStatus(
    bool FeedRegistered,
    bool StreamEndpointEnabled,
    bool IntentEndpointEnabled,
    string StreamEndpointPath,
    string SharedSnapshotEndpointPath,
    string IntentEndpointPath)
{
    /// <summary>
    /// Disabled stream status defaults.
    /// </summary>
    public static VapeCacheAdminEventStreamStatus Disabled { get; } = new(
        FeedRegistered: false,
        StreamEndpointEnabled: false,
        IntentEndpointEnabled: false,
        StreamEndpointPath: "/vapecache/api/stream",
        SharedSnapshotEndpointPath: "/vapecache/api/dashboard/shared-snapshot",
        IntentEndpointPath: "/vapecache/api/intent");
}

