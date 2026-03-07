using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Diagnostics;

namespace VapeCache.Console.Hosting;

internal sealed class SharedDashboardSnapshotPublisherHostedService(
    ICacheStats cacheStats,
    ICurrentCacheService currentCache,
    IRedisCircuitBreakerState breakerState,
    IRedisFailoverController failoverController,
    IEnumerable<IRedisMultiplexerDiagnostics> diagnostics,
    ISpillStoreDiagnostics? spillDiagnostics,
    IRedisCommandExecutor redis,
    ILogger<SharedDashboardSnapshotPublisherHostedService> logger)
    : BackgroundService, IHostedLifecycleService
{
    private readonly IRedisMultiplexerDiagnostics? _diagnostic = GetFirstOrDefault(diagnostics);

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Shared dashboard snapshot publisher starting.");
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Shared dashboard snapshot publisher started.");
        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Shared dashboard snapshot publisher stopping.");
        return Task.CompletedTask;
    }

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Shared dashboard snapshot publisher stopped.");
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 500ms cadence keeps dashboard responsive without overloading Redis.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));

        while (!stoppingToken.IsCancellationRequested &&
               await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await PublishSnapshotAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async ValueTask PublishSnapshotAsync(CancellationToken ct)
    {
        try
        {
            var snapshot = BuildSnapshot();
            var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                snapshot,
                VapeCacheSharedDashboardSnapshotJsonContext.Default.VapeCacheSharedDashboardSnapshot);

            await redis.SetAsync(
                    VapeCacheSharedDashboardSnapshotStore.RedisKey,
                    payload,
                    VapeCacheSharedDashboardSnapshotStore.TimeToLive,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to publish shared dashboard snapshot.");
        }
    }

    private VapeCacheSharedDashboardSnapshot BuildSnapshot()
    {
        var stats = cacheStats.Snapshot;
        var reads = stats.Hits + stats.Misses;
        var writes = stats.SetCalls + stats.RemoveCalls;
        var autoscaler = _diagnostic?.GetAutoscalerSnapshot();
        var lanes = _diagnostic?.GetMuxLaneSnapshots() ?? Array.Empty<RedisMuxLaneSnapshot>();
        var spill = spillDiagnostics?.GetSnapshot();

        return new VapeCacheSharedDashboardSnapshot(
            TimestampUtc: DateTimeOffset.UtcNow,
            Backend: BackendTypeResolver.Resolve(
                currentCache.CurrentName,
                breakerState.IsOpen,
                failoverController.IsForcedOpen),
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
            BreakerEnabled: breakerState.Enabled,
            BreakerOpen: breakerState.IsOpen,
            BreakerConsecutiveFailures: breakerState.ConsecutiveFailures,
            BreakerOpenRemaining: breakerState.OpenRemaining,
            BreakerForcedOpen: failoverController.IsForcedOpen,
            BreakerReason: failoverController.Reason,
            Autoscaler: autoscaler,
            Lanes: lanes,
            Spill: spill);
    }

    private static IRedisMultiplexerDiagnostics? GetFirstOrDefault(IEnumerable<IRedisMultiplexerDiagnostics> source)
    {
        using var enumerator = source.GetEnumerator();
        return enumerator.MoveNext() ? enumerator.Current : null;
    }
}
