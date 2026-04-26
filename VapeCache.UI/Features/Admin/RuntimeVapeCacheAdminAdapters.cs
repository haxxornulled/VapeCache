using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Diagnostics;
using VapeCache.Extensions.Aspire;

namespace VapeCache.UI.Features.Admin;

/// <summary>
/// Default runtime-backed stats snapshot provider.
/// </summary>
internal sealed class RuntimeVapeCacheAdminStatsSnapshotProvider : IVapeCacheAdminStatsSnapshotProvider
{
    private readonly ICacheBackendState _backendState;
    private readonly ICacheStats _stats;
    private readonly IRedisCommandExecutor _redis;
    private readonly IRedisMultiplexerDiagnostics? _diagnostics;

    public RuntimeVapeCacheAdminStatsSnapshotProvider(
        ICacheBackendState backendState,
        ICacheStats stats,
        IRedisCommandExecutor redis,
        IEnumerable<IRedisMultiplexerDiagnostics> diagnostics)
    {
        _backendState = backendState ?? throw new ArgumentNullException(nameof(backendState));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _diagnostics = diagnostics?.FirstOrDefault();
    }

    public VapeCacheAdminStatsSnapshot GetSnapshot()
    {
        var shared = RuntimeVapeCacheSharedSnapshotReader.TryReadAsync(_redis).GetAwaiter().GetResult();
        if (shared is not null)
        {
            return new VapeCacheAdminStatsSnapshot(
                TimestampUtc: shared.TimestampUtc,
                Backend: shared.Backend,
                Stats: new CacheStatsSnapshot(
                    GetCalls: shared.Reads,
                    Hits: shared.Hits,
                    Misses: shared.Misses,
                    SetCalls: shared.Writes,
                    RemoveCalls: 0,
                    FallbackToMemory: shared.FallbackToMemory,
                    RedisBreakerOpened: shared.RedisBreakerOpened,
                    StampedeKeyRejected: shared.StampedeKeyRejected,
                    StampedeLockWaitTimeout: shared.StampedeLockWaitTimeout,
                    StampedeFailureBackoffRejected: shared.StampedeFailureBackoffRejected),
                Reads: shared.Reads,
                Writes: shared.Writes,
                HitRate: shared.HitRate,
                Lanes: shared.Lanes,
                HealthyLaneCount: shared.Lanes.Count(lane => lane.Healthy));
        }

        var stats = _stats.Snapshot;
        var reads = stats.Hits + stats.Misses;
        var writes = stats.SetCalls + stats.RemoveCalls;
        var hitRate = reads <= 0 ? 0d : (double)stats.Hits / reads;
        var lanes = _diagnostics?.GetMuxLaneSnapshots() ?? Array.Empty<RedisMuxLaneSnapshot>();

        var healthyLaneCount = 0;
        for (var i = 0; i < lanes.Count; i++)
        {
            if (lanes[i].Healthy)
                healthyLaneCount++;
        }

        return new VapeCacheAdminStatsSnapshot(
            TimestampUtc: DateTimeOffset.UtcNow,
            Backend: _backendState.EffectiveBackend,
            Stats: stats,
            Reads: reads,
            Writes: writes,
            HitRate: hitRate,
            Lanes: lanes,
            HealthyLaneCount: healthyLaneCount);
    }
}

/// <summary>
/// Default runtime-backed invalidation operations adapter.
/// </summary>
internal sealed class RuntimeVapeCacheAdminInvalidationOperationsFacade : IVapeCacheAdminInvalidationOperationsFacade
{
    private readonly IVapeCache _cache;

    public RuntimeVapeCacheAdminInvalidationOperationsFacade(IVapeCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct = default)
        => _cache.InvalidateTagAsync(tag, ct);

    public ValueTask<long> InvalidateZoneAsync(string zone, CancellationToken ct = default)
        => _cache.InvalidateZoneAsync(zone, ct);

    public ValueTask<bool> InvalidateKeyAsync(string key, CancellationToken ct = default)
        => _cache.RemoveAsync(new CacheKey(key), ct);
}

/// <summary>
/// Default runtime-backed autoscaler status provider.
/// </summary>
internal sealed class RuntimeVapeCacheAdminAutoscalerStatusProvider : IVapeCacheAdminAutoscalerStatusProvider
{
    private readonly IRedisMultiplexerDiagnostics? _diagnostics;
    private readonly IRedisCommandExecutor _redis;

    public RuntimeVapeCacheAdminAutoscalerStatusProvider(
        IRedisCommandExecutor redis,
        IEnumerable<IRedisMultiplexerDiagnostics> diagnostics)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _diagnostics = diagnostics?.FirstOrDefault();
    }

    public RedisAutoscalerSnapshot? GetStatus()
    {
        var shared = RuntimeVapeCacheSharedSnapshotReader.TryReadAsync(_redis).GetAwaiter().GetResult();
        if (shared?.Autoscaler is not null)
            return shared.Autoscaler;

        return _diagnostics?.GetAutoscalerSnapshot();
    }
}

/// <summary>
/// Default runtime-backed spill diagnostics provider.
/// </summary>
internal sealed class RuntimeVapeCacheAdminSpillDiagnosticsProvider : IVapeCacheAdminSpillDiagnosticsProvider
{
    private readonly ISpillStoreDiagnostics? _spillDiagnostics;
    private readonly IRedisCommandExecutor _redis;

    public RuntimeVapeCacheAdminSpillDiagnosticsProvider(
        IRedisCommandExecutor redis,
        ISpillStoreDiagnostics? spillDiagnostics = null)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _spillDiagnostics = spillDiagnostics;
    }

    public SpillStoreDiagnosticsSnapshot? GetStatus()
    {
        var shared = RuntimeVapeCacheSharedSnapshotReader.TryReadAsync(_redis).GetAwaiter().GetResult();
        if (shared?.Spill is not null)
            return shared.Spill;

        return _spillDiagnostics?.GetSnapshot();
    }
}

/// <summary>
/// Default runtime-backed reconciliation status provider.
/// </summary>
internal sealed class RuntimeVapeCacheAdminReconciliationStatusProvider : IVapeCacheAdminReconciliationStatusProvider
{
    private readonly IRedisReconciliationService? _reconciliation;

    public RuntimeVapeCacheAdminReconciliationStatusProvider(IRedisReconciliationService? reconciliation = null)
    {
        _reconciliation = reconciliation;
    }

    public VapeCacheAdminReconciliationStatus GetStatus()
        => new(
            Enabled: _reconciliation is not null,
            PendingOperations: _reconciliation?.PendingOperations ?? 0);

    public async ValueTask<bool> ReconcileAsync(CancellationToken ct = default)
    {
        if (_reconciliation is null)
            return false;

        await _reconciliation.ReconcileAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<bool> FlushAsync(CancellationToken ct = default)
    {
        if (_reconciliation is null)
            return false;

        await _reconciliation.FlushAsync(ct).ConfigureAwait(false);
        return true;
    }
}

/// <summary>
/// Default runtime-backed breaker status provider.
/// </summary>
internal sealed class RuntimeVapeCacheAdminBreakerStatusProvider : IVapeCacheAdminBreakerStatusProvider
{
    private readonly IRedisCircuitBreakerState _breaker;
    private readonly IRedisFailoverController _failover;
    private readonly IRedisCommandExecutor _redis;

    public RuntimeVapeCacheAdminBreakerStatusProvider(
        IRedisCommandExecutor redis,
        IRedisCircuitBreakerState breaker,
        IRedisFailoverController failover)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
        _failover = failover ?? throw new ArgumentNullException(nameof(failover));
    }

    public VapeCacheAdminBreakerStatus GetStatus()
    {
        var shared = RuntimeVapeCacheSharedSnapshotReader.TryReadAsync(_redis).GetAwaiter().GetResult();
        if (shared is not null)
        {
            return new VapeCacheAdminBreakerStatus(
                Enabled: shared.BreakerEnabled,
                IsOpen: shared.BreakerOpen,
                ConsecutiveFailures: shared.BreakerConsecutiveFailures,
                OpenRemaining: shared.BreakerOpenRemaining,
                HalfOpenProbeInFlight: false,
                IsForcedOpen: shared.BreakerForcedOpen,
                Reason: shared.BreakerReason);
        }

        return new VapeCacheAdminBreakerStatus(
            Enabled: _breaker.Enabled,
            IsOpen: _breaker.IsOpen,
            ConsecutiveFailures: _breaker.ConsecutiveFailures,
            OpenRemaining: _breaker.OpenRemaining,
            HalfOpenProbeInFlight: _breaker.HalfOpenProbeInFlight,
            IsForcedOpen: _failover.IsForcedOpen,
            Reason: _failover.Reason);
    }
}

/// <summary>
/// Default runtime-backed policy inspection provider.
/// </summary>
internal sealed class RuntimeVapeCacheAdminPolicyInspectionProvider : IVapeCacheAdminPolicyInspectionProvider
{
    private readonly ICacheIntentRegistry _intentRegistry;

    public RuntimeVapeCacheAdminPolicyInspectionProvider(ICacheIntentRegistry intentRegistry)
    {
        _intentRegistry = intentRegistry ?? throw new ArgumentNullException(nameof(intentRegistry));
    }

    public IReadOnlyList<CacheIntentEntry> GetRecent(int take = 100)
        => _intentRegistry.GetRecent(Math.Clamp(take, 1, 500));
}

/// <summary>
/// Default runtime-backed event stream status provider.
/// </summary>
internal sealed class RuntimeVapeCacheAdminEventStreamFeedProvider : IVapeCacheAdminEventStreamFeedProvider
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly IVapeCacheLiveMetricsFeed? _liveFeed;

    public RuntimeVapeCacheAdminEventStreamFeedProvider(
        IHostEnvironment environment,
        IConfiguration configuration,
        IVapeCacheLiveMetricsFeed? liveFeed = null)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _liveFeed = liveFeed;
    }

    public VapeCacheAdminEventStreamStatus GetStatus()
    {
        var streamEndpointEnabled =
            _environment.IsDevelopment()
            || _configuration.GetValue<bool>("VapeCache:Endpoints:EnableLiveStream");
        var intentEndpointEnabled =
            _environment.IsDevelopment()
            || _configuration.GetValue<bool>("VapeCache:Endpoints:EnableIntentEndpoints");

        return new VapeCacheAdminEventStreamStatus(
            FeedRegistered: _liveFeed is not null,
            StreamEndpointEnabled: streamEndpointEnabled,
            IntentEndpointEnabled: intentEndpointEnabled,
            StreamEndpointPath: "/vapecache/api/stream",
            SharedSnapshotEndpointPath: "/vapecache/api/dashboard/shared-snapshot",
            IntentEndpointPath: "/vapecache/api/intent");
    }
}

internal static class RuntimeVapeCacheSharedSnapshotReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async ValueTask<VapeCacheSharedDashboardSnapshot?> TryReadAsync(
        IRedisCommandExecutor redis,
        CancellationToken ct = default)
    {
        try
        {
            var payload = await redis.GetAsync(VapeCacheSharedDashboardSnapshotStore.RedisKey, ct).ConfigureAwait(false);
            if (payload is null || payload.Length == 0)
                return null;

            var snapshot = JsonSerializer.Deserialize<VapeCacheSharedDashboardSnapshot>(payload, JsonOptions);
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
            return null;
        }
    }
}
