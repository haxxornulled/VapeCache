using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Diagnostics;

namespace VapeCache.UI.Features.Dashboard;

public sealed class VapeCacheDashboardOrchestrator
{
    private readonly ICacheStats _cacheStats;
    private readonly ICurrentCacheService _currentCache;
    private readonly IRedisCircuitBreakerState _breakerState;
    private readonly IRedisFailoverController _failoverController;
    private readonly IRedisCommandExecutor _redis;
    private readonly IRedisMultiplexerDiagnostics? _diagnostic;
    private readonly ISpillStoreDiagnostics? _spillDiagnostics;
    private readonly object _cacheGate = new();
    private DateTimeOffset _sharedCacheTimestampUtc;
    private VapeCacheSharedDashboardSnapshot? _sharedCachePayload;
    private VapeCacheDashboardSnapshot? _sharedCacheSnapshot;
    private CacheStatsSnapshot _localCacheStats;
    private BackendType _localCacheBackend;
    private bool _localCacheBreakerEnabled;
    private bool _localCacheBreakerOpen;
    private int _localCacheBreakerConsecutiveFailures;
    private TimeSpan? _localCacheBreakerOpenRemaining;
    private bool _localCacheBreakerForcedOpen;
    private string? _localCacheBreakerReason;
    private RedisAutoscalerSnapshot? _localCacheAutoscaler;
    private SpillStoreDiagnosticsSnapshot? _localCacheSpill;
    private IReadOnlyList<RedisMuxLaneSnapshot>? _localCacheLanes;
    private VapeCacheDashboardSnapshot? _localCacheSnapshot;

    public VapeCacheDashboardOrchestrator(
        ICacheStats cacheStats,
        ICurrentCacheService currentCache,
        IRedisCircuitBreakerState breakerState,
        IRedisFailoverController failoverController,
        IRedisCommandExecutor redis,
        IEnumerable<IRedisMultiplexerDiagnostics> diagnostics,
        ISpillStoreDiagnostics? spillDiagnostics = null)
    {
        _cacheStats = cacheStats;
        _currentCache = currentCache;
        _breakerState = breakerState;
        _failoverController = failoverController;
        _redis = redis;
        _diagnostic = GetFirstOrDefault(diagnostics);
        _spillDiagnostics = spillDiagnostics;
    }

    public async ValueTask<VapeCacheDashboardSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var shared = await TryGetSharedSnapshotAsync(ct).ConfigureAwait(false);
        if (shared is not null)
            return GetOrCreateSharedSnapshot(shared);

        var snapshot = _cacheStats.Snapshot;

        var autoscaler = _diagnostic?.GetAutoscalerSnapshot();
        var lanes = _diagnostic?.GetMuxLaneSnapshots() ?? Array.Empty<RedisMuxLaneSnapshot>();
        var spill = _spillDiagnostics?.GetSnapshot();
        var breakerEnabled = _breakerState.Enabled;
        var breakerOpen = _breakerState.IsOpen;
        var breakerConsecutiveFailures = _breakerState.ConsecutiveFailures;
        var breakerOpenRemaining = _breakerState.OpenRemaining;
        var breakerForcedOpen = _failoverController.IsForcedOpen;
        var breakerReason = _failoverController.Reason;
        var backend = ResolveDisplayBackend(
            _currentCache.CurrentName,
            breakerOpen,
            breakerForcedOpen);

        return GetOrCreateLocalSnapshot(
            snapshot,
            backend,
            autoscaler,
            lanes,
            spill,
            breakerEnabled,
            breakerOpen,
            breakerConsecutiveFailures,
            breakerOpenRemaining,
            breakerForcedOpen,
            breakerReason);
    }

    private async ValueTask<VapeCacheSharedDashboardSnapshot?> TryGetSharedSnapshotAsync(CancellationToken ct)
    {
        try
        {
            using var payload = await _redis.GetLeaseAsync(VapeCacheSharedDashboardSnapshotStore.RedisKey, ct).ConfigureAwait(false);
            if (payload is null || payload.IsNull || payload.Length == 0)
                return null;

            var snapshot = System.Text.Json.JsonSerializer.Deserialize(
                payload.Span,
                VapeCacheSharedDashboardSnapshotJsonContext.Default.VapeCacheSharedDashboardSnapshot);
            if (snapshot is null)
                return null;

            if (DateTimeOffset.UtcNow - snapshot.TimestampUtc > VapeCacheSharedDashboardSnapshotStore.MaxSnapshotAge)
                return null;

            return snapshot;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            // Fall back to in-process snapshot if shared channel is unavailable.
            return null;
        }
    }

    private VapeCacheDashboardSnapshot GetOrCreateSharedSnapshot(VapeCacheSharedDashboardSnapshot snapshot)
    {
        lock (_cacheGate)
        {
            if (_sharedCacheSnapshot is not null &&
                _sharedCacheTimestampUtc == snapshot.TimestampUtc)
            {
                return _sharedCacheSnapshot;
            }

            if (_sharedCacheSnapshot is not null &&
                _sharedCachePayload is not null &&
                IsEquivalentSharedPayload(snapshot, _sharedCachePayload))
            {
                _sharedCacheTimestampUtc = snapshot.TimestampUtc;
                _sharedCachePayload = snapshot;
                return _sharedCacheSnapshot;
            }

            var mapped = new VapeCacheDashboardSnapshot(
                TimestampUtc: snapshot.TimestampUtc,
                Backend: ResolveDisplayBackend(snapshot.Backend, snapshot.BreakerOpen, snapshot.BreakerForcedOpen),
                HitRate: snapshot.HitRate,
                Reads: snapshot.Reads,
                Writes: snapshot.Writes,
                Hits: snapshot.Hits,
                Misses: snapshot.Misses,
                FallbackToMemory: snapshot.FallbackToMemory,
                RedisBreakerOpened: snapshot.RedisBreakerOpened,
                StampedeKeyRejected: snapshot.StampedeKeyRejected,
                StampedeLockWaitTimeout: snapshot.StampedeLockWaitTimeout,
                StampedeFailureBackoffRejected: snapshot.StampedeFailureBackoffRejected,
                BreakerEnabled: snapshot.BreakerEnabled,
                BreakerOpen: snapshot.BreakerOpen,
                BreakerConsecutiveFailures: snapshot.BreakerConsecutiveFailures,
                BreakerOpenRemaining: snapshot.BreakerOpenRemaining,
                BreakerForcedOpen: snapshot.BreakerForcedOpen,
                BreakerReason: snapshot.BreakerReason,
                Autoscaler: snapshot.Autoscaler,
                Lanes: snapshot.Lanes,
                Spill: snapshot.Spill);

            _sharedCacheTimestampUtc = snapshot.TimestampUtc;
            _sharedCachePayload = snapshot;
            _sharedCacheSnapshot = mapped;
            return mapped;
        }
    }

    private VapeCacheDashboardSnapshot GetOrCreateLocalSnapshot(
        CacheStatsSnapshot stats,
        BackendType backend,
        RedisAutoscalerSnapshot? autoscaler,
        IReadOnlyList<RedisMuxLaneSnapshot> lanes,
        SpillStoreDiagnosticsSnapshot? spill,
        bool breakerEnabled,
        bool breakerOpen,
        int breakerConsecutiveFailures,
        TimeSpan? breakerOpenRemaining,
        bool breakerForcedOpen,
        string? breakerReason)
    {
        lock (_cacheGate)
        {
            if (_localCacheSnapshot is not null &&
                stats.Equals(_localCacheStats) &&
                backend == _localCacheBackend &&
                breakerEnabled == _localCacheBreakerEnabled &&
                breakerOpen == _localCacheBreakerOpen &&
                breakerConsecutiveFailures == _localCacheBreakerConsecutiveFailures &&
                breakerOpenRemaining == _localCacheBreakerOpenRemaining &&
                breakerForcedOpen == _localCacheBreakerForcedOpen &&
                string.Equals(breakerReason, _localCacheBreakerReason, StringComparison.Ordinal) &&
                Equals(autoscaler, _localCacheAutoscaler) &&
                Equals(spill, _localCacheSpill) &&
                AreEquivalentLanes(lanes, _localCacheLanes))
            {
                return _localCacheSnapshot;
            }

            var reads = stats.Hits + stats.Misses;
            var writes = stats.SetCalls + stats.RemoveCalls;
            var snapshot = new VapeCacheDashboardSnapshot(
                TimestampUtc: DateTimeOffset.UtcNow,
                Backend: backend,
                HitRate: reads <= 0 ? 0d : (double)stats.Hits / reads,
                Reads: reads,
                Writes: writes,
                Hits: stats.Hits,
                Misses: stats.Misses,
                FallbackToMemory: stats.FallbackToMemory,
                RedisBreakerOpened: stats.RedisBreakerOpened,
                StampedeKeyRejected: stats.StampedeKeyRejected,
                StampedeLockWaitTimeout: stats.StampedeLockWaitTimeout,
                StampedeFailureBackoffRejected: stats.StampedeFailureBackoffRejected,
                BreakerEnabled: breakerEnabled,
                BreakerOpen: breakerOpen,
                BreakerConsecutiveFailures: breakerConsecutiveFailures,
                BreakerOpenRemaining: breakerOpenRemaining,
                BreakerForcedOpen: breakerForcedOpen,
                BreakerReason: breakerReason,
                Autoscaler: autoscaler,
                Lanes: lanes,
                Spill: spill);

            _localCacheStats = stats;
            _localCacheBackend = backend;
            _localCacheBreakerEnabled = breakerEnabled;
            _localCacheBreakerOpen = breakerOpen;
            _localCacheBreakerConsecutiveFailures = breakerConsecutiveFailures;
            _localCacheBreakerOpenRemaining = breakerOpenRemaining;
            _localCacheBreakerForcedOpen = breakerForcedOpen;
            _localCacheBreakerReason = breakerReason;
            _localCacheAutoscaler = autoscaler;
            _localCacheSpill = spill;
            _localCacheLanes = lanes;
            _localCacheSnapshot = snapshot;
            return snapshot;
        }
    }

    private static bool AreEquivalentLanes(
        IReadOnlyList<RedisMuxLaneSnapshot> current,
        IReadOnlyList<RedisMuxLaneSnapshot>? previous)
    {
        if (ReferenceEquals(current, previous))
            return true;

        if (previous is null || current.Count != previous.Count)
            return false;

        for (var i = 0; i < current.Count; i++)
        {
            if (!Equals(current[i], previous[i]))
                return false;
        }

        return true;
    }

    private static bool IsEquivalentSharedPayload(
        VapeCacheSharedDashboardSnapshot current,
        VapeCacheSharedDashboardSnapshot previous)
    {
        return current.Backend == previous.Backend &&
               current.HitRate.Equals(previous.HitRate) &&
               current.Reads == previous.Reads &&
               current.Writes == previous.Writes &&
               current.Hits == previous.Hits &&
               current.Misses == previous.Misses &&
               current.FallbackToMemory == previous.FallbackToMemory &&
               current.RedisBreakerOpened == previous.RedisBreakerOpened &&
               current.StampedeKeyRejected == previous.StampedeKeyRejected &&
               current.StampedeLockWaitTimeout == previous.StampedeLockWaitTimeout &&
               current.StampedeFailureBackoffRejected == previous.StampedeFailureBackoffRejected &&
               current.BreakerEnabled == previous.BreakerEnabled &&
               current.BreakerOpen == previous.BreakerOpen &&
               current.BreakerConsecutiveFailures == previous.BreakerConsecutiveFailures &&
               current.BreakerOpenRemaining == previous.BreakerOpenRemaining &&
               current.BreakerForcedOpen == previous.BreakerForcedOpen &&
               string.Equals(current.BreakerReason, previous.BreakerReason, StringComparison.Ordinal) &&
               Equals(current.Autoscaler, previous.Autoscaler) &&
               Equals(current.Spill, previous.Spill) &&
               AreEquivalentLanes(current.Lanes, previous.Lanes);
    }

    private static BackendType ResolveDisplayBackend(string? currentBackend, bool breakerOpen, bool forcedOpen)
    {
        return BackendTypeResolver.Resolve(currentBackend, breakerOpen, forcedOpen);
    }

    private static BackendType ResolveDisplayBackend(BackendType currentBackend, bool breakerOpen, bool forcedOpen)
    {
        if (forcedOpen || breakerOpen)
            return BackendType.InMemory;

        return currentBackend;
    }

    private static IRedisMultiplexerDiagnostics? GetFirstOrDefault(IEnumerable<IRedisMultiplexerDiagnostics> source)
    {
        using var enumerator = source.GetEnumerator();
        return enumerator.MoveNext() ? enumerator.Current : null;
    }
}

public sealed record VapeCacheDashboardSnapshot(
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
    public static VapeCacheDashboardSnapshot Empty { get; } = new(
        TimestampUtc: DateTimeOffset.MinValue,
        Backend: BackendType.Redis,
        HitRate: 0d,
        Reads: 0,
        Writes: 0,
        Hits: 0,
        Misses: 0,
        FallbackToMemory: 0,
        RedisBreakerOpened: 0,
        StampedeKeyRejected: 0,
        StampedeLockWaitTimeout: 0,
        StampedeFailureBackoffRejected: 0,
        BreakerEnabled: false,
        BreakerOpen: false,
        BreakerConsecutiveFailures: 0,
        BreakerOpenRemaining: null,
        BreakerForcedOpen: false,
        BreakerReason: null,
        Autoscaler: null,
        Lanes: Array.Empty<RedisMuxLaneSnapshot>(),
        Spill: null);
}
