using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Diagnostics;
using VapeCache.Extensions.Aspire.Hosting;

namespace VapeCache.Extensions.Aspire;

internal sealed partial class VapeCacheSharedDashboardSnapshotPublisher : HostedLifecycleLoopService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ICacheStats _stats;
    private readonly ICacheBackendState _backendState;
    private readonly IRedisCircuitBreakerState? _breakerState;
    private readonly IRedisFailoverController? _failoverController;
    private readonly IRedisCommandExecutor _redis;
    private readonly IOptionsMonitor<VapeCacheEndpointOptions> _options;
    private readonly ISpillStoreDiagnostics? _spillDiagnostics;
    private readonly IRedisMultiplexerDiagnostics? _diagnostics;
    private readonly ICacheOriginStats? _originStats;
    private readonly ILogger<VapeCacheSharedDashboardSnapshotPublisher> _logger;

    public VapeCacheSharedDashboardSnapshotPublisher(
        ICacheStats stats,
        ICacheBackendState backendState,
        IRedisCommandExecutor redis,
        IOptionsMonitor<VapeCacheEndpointOptions> options,
        ILogger<VapeCacheSharedDashboardSnapshotPublisher> logger,
        IRedisCircuitBreakerState? breakerState = null,
        IRedisFailoverController? failoverController = null,
        ISpillStoreDiagnostics? spillDiagnostics = null,
        IRedisMultiplexerDiagnostics? diagnostics = null,
        ICacheOriginStats? originStats = null)
    {
        _stats = stats;
        _backendState = backendState;
        _redis = redis;
        _options = options;
        _logger = logger;
        _breakerState = breakerState;
        _failoverController = failoverController;
        _spillDiagnostics = spillDiagnostics;
        _diagnostics = diagnostics;
        _originStats = originStats;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _options.CurrentValue;
            var interval = options.SharedSnapshotPublishInterval <= TimeSpan.Zero
                ? TimeSpan.FromSeconds(1)
                : options.SharedSnapshotPublishInterval;

            if (options.PublishSharedSnapshot)
            {
                await PublishSnapshotAsync(stoppingToken).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PublishSnapshotAsync(CancellationToken ct)
    {
        try
        {
            var stats = _stats.Snapshot;
            var reads = stats.Hits + stats.Misses;
            var writes = stats.SetCalls + stats.RemoveCalls;
            var breakerEnabled = _breakerState?.Enabled ?? false;
            var breakerOpen = _breakerState?.IsOpen ?? false;
            var breakerConsecutiveFailures = _breakerState?.ConsecutiveFailures ?? 0;
            var breakerOpenRemaining = _breakerState?.OpenRemaining;
            var breakerForcedOpen = _failoverController?.IsForcedOpen ?? false;
            var breakerReason = _failoverController?.Reason;
            var snapshot = new VapeCacheSharedDashboardSnapshot(
                TimestampUtc: DateTimeOffset.UtcNow,
                Backend: _backendState.EffectiveBackend,
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
                Autoscaler: _diagnostics?.GetAutoscalerSnapshot(),
                Lanes: _diagnostics?.GetMuxLaneSnapshots() ?? Array.Empty<RedisMuxLaneSnapshot>(),
                Spill: _spillDiagnostics?.GetSnapshot(),
                OriginStats: _originStats?.Snapshot ?? default);

            var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
            _ = await _redis.SetAsync(
                VapeCacheSharedDashboardSnapshotStore.RedisKey,
                payload,
                VapeCacheSharedDashboardSnapshotStore.TimeToLive,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogSharedSnapshotPublishFailed(_logger, ex);
        }
    }

    [LoggerMessage(
        EventId = 9040,
        Level = LogLevel.Debug,
        Message = "Unable to publish shared VapeCache dashboard snapshot.")]
    private static partial void LogSharedSnapshotPublishFailed(ILogger logger, Exception exception);
}
