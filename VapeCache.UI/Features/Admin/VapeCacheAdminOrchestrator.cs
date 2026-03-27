using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Diagnostics;

namespace VapeCache.UI.Features.Admin;

/// <summary>
/// Adapter-layer orchestration for the Blazor admin surface.
/// Keeps runtime behavior in runtime services and exposes UI-ready projections.
/// </summary>
public sealed class VapeCacheAdminOrchestrator
{
    private readonly IVapeCacheAdminStatsSnapshotProvider _statsSnapshotProvider;
    private readonly IVapeCacheAdminInvalidationOperationsFacade _invalidationOperations;
    private readonly IVapeCacheAdminAutoscalerStatusProvider _autoscalerStatusProvider;
    private readonly IVapeCacheAdminSpillDiagnosticsProvider _spillDiagnosticsProvider;
    private readonly IVapeCacheAdminReconciliationStatusProvider _reconciliationStatusProvider;
    private readonly IVapeCacheAdminBreakerStatusProvider _breakerStatusProvider;
    private readonly IVapeCacheAdminPolicyInspectionProvider _policyInspectionProvider;
    private readonly IVapeCacheAdminEventStreamFeedProvider _eventStreamFeedProvider;

    /// <summary>
    /// Creates a new admin orchestrator.
    /// </summary>
    public VapeCacheAdminOrchestrator(
        IVapeCacheAdminStatsSnapshotProvider statsSnapshotProvider,
        IVapeCacheAdminInvalidationOperationsFacade invalidationOperations,
        IVapeCacheAdminAutoscalerStatusProvider autoscalerStatusProvider,
        IVapeCacheAdminSpillDiagnosticsProvider spillDiagnosticsProvider,
        IVapeCacheAdminReconciliationStatusProvider reconciliationStatusProvider,
        IVapeCacheAdminBreakerStatusProvider breakerStatusProvider,
        IVapeCacheAdminPolicyInspectionProvider policyInspectionProvider,
        IVapeCacheAdminEventStreamFeedProvider eventStreamFeedProvider)
    {
        _statsSnapshotProvider = statsSnapshotProvider ?? throw new ArgumentNullException(nameof(statsSnapshotProvider));
        _invalidationOperations = invalidationOperations ?? throw new ArgumentNullException(nameof(invalidationOperations));
        _autoscalerStatusProvider = autoscalerStatusProvider ?? throw new ArgumentNullException(nameof(autoscalerStatusProvider));
        _spillDiagnosticsProvider = spillDiagnosticsProvider ?? throw new ArgumentNullException(nameof(spillDiagnosticsProvider));
        _reconciliationStatusProvider = reconciliationStatusProvider ?? throw new ArgumentNullException(nameof(reconciliationStatusProvider));
        _breakerStatusProvider = breakerStatusProvider ?? throw new ArgumentNullException(nameof(breakerStatusProvider));
        _policyInspectionProvider = policyInspectionProvider ?? throw new ArgumentNullException(nameof(policyInspectionProvider));
        _eventStreamFeedProvider = eventStreamFeedProvider ?? throw new ArgumentNullException(nameof(eventStreamFeedProvider));
    }

    /// <summary>
    /// Gets the current admin snapshot.
    /// </summary>
    public ValueTask<VapeCacheAdminSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        _ = ct;

        var statsSnapshot = _statsSnapshotProvider.GetSnapshot();
        var breaker = _breakerStatusProvider.GetStatus();
        var reconciliation = _reconciliationStatusProvider.GetStatus();

        var sample = new VapeCacheAdminSnapshot(
            TimestampUtc: statsSnapshot.TimestampUtc,
            Backend: statsSnapshot.Backend,
            Reads: statsSnapshot.Reads,
            Writes: statsSnapshot.Writes,
            HitRate: statsSnapshot.HitRate,
            Stats: statsSnapshot.Stats,
            BreakerEnabled: breaker.Enabled,
            BreakerOpen: breaker.IsOpen,
            BreakerConsecutiveFailures: breaker.ConsecutiveFailures,
            BreakerOpenRemaining: breaker.OpenRemaining,
            BreakerHalfOpenProbeInFlight: breaker.HalfOpenProbeInFlight,
            BreakerForcedOpen: breaker.IsForcedOpen,
            BreakerReason: breaker.Reason,
            Autoscaler: _autoscalerStatusProvider.GetStatus(),
            Lanes: statsSnapshot.Lanes,
            HealthyLaneCount: statsSnapshot.HealthyLaneCount,
            Spill: _spillDiagnosticsProvider.GetStatus(),
            ReconciliationPendingOperations: reconciliation.PendingOperations,
            ReconciliationEnabled: reconciliation.Enabled);

        return ValueTask.FromResult(sample);
    }

    /// <summary>
    /// Invalidates a cache tag and returns the new version.
    /// </summary>
    public ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct = default)
        => _invalidationOperations.InvalidateTagAsync(NormalizeRequired(tag, nameof(tag)), ct);

    /// <summary>
    /// Invalidates a cache zone and returns the new version.
    /// </summary>
    public ValueTask<long> InvalidateZoneAsync(string zone, CancellationToken ct = default)
        => _invalidationOperations.InvalidateZoneAsync(NormalizeRequired(zone, nameof(zone)), ct);

    /// <summary>
    /// Invalidates a specific key.
    /// </summary>
    public ValueTask<bool> InvalidateKeyAsync(string key, CancellationToken ct = default)
        => _invalidationOperations.InvalidateKeyAsync(NormalizeRequired(key, nameof(key)), ct);

    /// <summary>
    /// Gets recent policy/intention entries.
    /// </summary>
    public IReadOnlyList<CacheIntentEntry> GetRecentPolicies(int take = 100)
        => _policyInspectionProvider.GetRecent(take);

    /// <summary>
    /// Gets event/stream feed status.
    /// </summary>
    public VapeCacheAdminEventStreamStatus GetEventStreamStatus()
        => _eventStreamFeedProvider.GetStatus();

    /// <summary>
    /// Triggers reconciliation if reconciliation service is available.
    /// </summary>
    public async ValueTask<bool> ReconcileAsync(CancellationToken ct = default)
        => await _reconciliationStatusProvider.ReconcileAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Flushes reconciliation state if reconciliation service is available.
    /// </summary>
    public async ValueTask<bool> FlushReconciliationAsync(CancellationToken ct = default)
        => await _reconciliationStatusProvider.FlushAsync(ct).ConfigureAwait(false);

    private static string NormalizeRequired(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value is required.", paramName);
        return value.Trim();
    }
}

/// <summary>
/// UI projection of runtime state used by the Blazor admin pages.
/// </summary>
public sealed record VapeCacheAdminSnapshot
{
    public VapeCacheAdminSnapshot(
        DateTimeOffset TimestampUtc,
        BackendType Backend,
        long Reads,
        long Writes,
        double HitRate,
        CacheStatsSnapshot Stats,
        bool BreakerEnabled,
        bool BreakerOpen,
        int BreakerConsecutiveFailures,
        TimeSpan? BreakerOpenRemaining,
        bool BreakerHalfOpenProbeInFlight,
        bool BreakerForcedOpen,
        string? BreakerReason,
        RedisAutoscalerSnapshot? Autoscaler,
        IReadOnlyList<RedisMuxLaneSnapshot> Lanes,
        int HealthyLaneCount,
        SpillStoreDiagnosticsSnapshot? Spill,
        int ReconciliationPendingOperations,
        bool ReconciliationEnabled)
    {
        this.TimestampUtc = TimestampUtc;
        this.Backend = Backend;
        this.Reads = Reads;
        this.Writes = Writes;
        this.HitRate = HitRate;
        this.Stats = Stats;
        this.BreakerEnabled = BreakerEnabled;
        this.BreakerOpen = BreakerOpen;
        this.BreakerConsecutiveFailures = BreakerConsecutiveFailures;
        this.BreakerOpenRemaining = BreakerOpenRemaining;
        this.BreakerHalfOpenProbeInFlight = BreakerHalfOpenProbeInFlight;
        this.BreakerForcedOpen = BreakerForcedOpen;
        this.BreakerReason = BreakerReason;
        this.Autoscaler = Autoscaler;
        this.Lanes = Lanes;
        this.HealthyLaneCount = HealthyLaneCount;
        this.Spill = Spill;
        this.ReconciliationPendingOperations = ReconciliationPendingOperations;
        this.ReconciliationEnabled = ReconciliationEnabled;
    }

    public DateTimeOffset TimestampUtc { get; init; }
    public BackendType Backend { get; init; }
    public long Reads { get; init; }
    public long Writes { get; init; }
    public double HitRate { get; init; }
    public CacheStatsSnapshot Stats { get; init; }
    public bool BreakerEnabled { get; init; }
    public bool BreakerOpen { get; init; }
    public int BreakerConsecutiveFailures { get; init; }
    public TimeSpan? BreakerOpenRemaining { get; init; }
    public bool BreakerHalfOpenProbeInFlight { get; init; }
    public bool BreakerForcedOpen { get; init; }
    public string? BreakerReason { get; init; }
    public RedisAutoscalerSnapshot? Autoscaler { get; init; }
    public IReadOnlyList<RedisMuxLaneSnapshot> Lanes { get; init; }
    public int HealthyLaneCount { get; init; }
    public SpillStoreDiagnosticsSnapshot? Spill { get; init; }
    public int ReconciliationPendingOperations { get; init; }
    public bool ReconciliationEnabled { get; init; }
}
