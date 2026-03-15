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
    private readonly IVapeCache _cache;
    private readonly ICacheBackendState _backendState;
    private readonly ICacheStats _stats;
    private readonly IRedisCircuitBreakerState _breaker;
    private readonly IRedisFailoverController _failover;
    private readonly ICacheIntentRegistry _intentRegistry;
    private readonly IRedisReconciliationService? _reconciliation;
    private readonly ISpillStoreDiagnostics? _spillDiagnostics;
    private readonly IRedisMultiplexerDiagnostics? _redisDiagnostics;

    /// <summary>
    /// Creates a new admin orchestrator.
    /// </summary>
    public VapeCacheAdminOrchestrator(
        IVapeCache cache,
        ICacheBackendState backendState,
        ICacheStats stats,
        IRedisCircuitBreakerState breaker,
        IRedisFailoverController failover,
        ICacheIntentRegistry intentRegistry,
        IEnumerable<IRedisMultiplexerDiagnostics> redisDiagnostics,
        IRedisReconciliationService? reconciliation = null,
        ISpillStoreDiagnostics? spillDiagnostics = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _backendState = backendState ?? throw new ArgumentNullException(nameof(backendState));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
        _failover = failover ?? throw new ArgumentNullException(nameof(failover));
        _intentRegistry = intentRegistry ?? throw new ArgumentNullException(nameof(intentRegistry));
        _reconciliation = reconciliation;
        _spillDiagnostics = spillDiagnostics;
        _redisDiagnostics = redisDiagnostics?.FirstOrDefault();
    }

    /// <summary>
    /// Gets the current admin snapshot.
    /// </summary>
    public ValueTask<VapeCacheAdminSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        _ = ct;

        var stats = _stats.Snapshot;
        var reads = stats.Hits + stats.Misses;
        var writes = stats.SetCalls + stats.RemoveCalls;
        var hitRate = reads <= 0 ? 0d : (double)stats.Hits / reads;

        var lanes = _redisDiagnostics?.GetMuxLaneSnapshots() ?? Array.Empty<RedisMuxLaneSnapshot>();
        var healthyLaneCount = 0;
        for (var i = 0; i < lanes.Count; i++)
        {
            if (lanes[i].Healthy)
                healthyLaneCount++;
        }

        var sample = new VapeCacheAdminSnapshot(
            TimestampUtc: DateTimeOffset.UtcNow,
            Backend: _backendState.EffectiveBackend,
            Reads: reads,
            Writes: writes,
            HitRate: hitRate,
            Stats: stats,
            BreakerEnabled: _breaker.Enabled,
            BreakerOpen: _breaker.IsOpen,
            BreakerConsecutiveFailures: _breaker.ConsecutiveFailures,
            BreakerOpenRemaining: _breaker.OpenRemaining,
            BreakerHalfOpenProbeInFlight: _breaker.HalfOpenProbeInFlight,
            BreakerForcedOpen: _failover.IsForcedOpen,
            BreakerReason: _failover.Reason,
            Autoscaler: _redisDiagnostics?.GetAutoscalerSnapshot(),
            Lanes: lanes,
            HealthyLaneCount: healthyLaneCount,
            Spill: _spillDiagnostics?.GetSnapshot(),
            ReconciliationPendingOperations: _reconciliation?.PendingOperations ?? 0,
            ReconciliationEnabled: _reconciliation is not null);

        return ValueTask.FromResult(sample);
    }

    /// <summary>
    /// Invalidates a cache tag and returns the new version.
    /// </summary>
    public ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct = default)
        => _cache.InvalidateTagAsync(NormalizeRequired(tag, nameof(tag)), ct);

    /// <summary>
    /// Invalidates a cache zone and returns the new version.
    /// </summary>
    public ValueTask<long> InvalidateZoneAsync(string zone, CancellationToken ct = default)
        => _cache.InvalidateZoneAsync(NormalizeRequired(zone, nameof(zone)), ct);

    /// <summary>
    /// Invalidates a specific key.
    /// </summary>
    public ValueTask<bool> InvalidateKeyAsync(string key, CancellationToken ct = default)
        => _cache.RemoveAsync(new CacheKey(NormalizeRequired(key, nameof(key))), ct);

    /// <summary>
    /// Gets recent policy/intention entries.
    /// </summary>
    public IReadOnlyList<CacheIntentEntry> GetRecentPolicies(int take = 100)
        => _intentRegistry.GetRecent(Math.Clamp(take, 1, 500));

    /// <summary>
    /// Triggers reconciliation if reconciliation service is available.
    /// </summary>
    public async ValueTask<bool> ReconcileAsync(CancellationToken ct = default)
    {
        if (_reconciliation is null)
            return false;

        await _reconciliation.ReconcileAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Flushes reconciliation state if reconciliation service is available.
    /// </summary>
    public async ValueTask<bool> FlushReconciliationAsync(CancellationToken ct = default)
    {
        if (_reconciliation is null)
            return false;

        await _reconciliation.FlushAsync(ct).ConfigureAwait(false);
        return true;
    }

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
public sealed record VapeCacheAdminSnapshot(
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
    bool ReconciliationEnabled);
