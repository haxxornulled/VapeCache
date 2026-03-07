using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Reconciliation;

/// <summary>
/// Background service that periodically runs reconciliation to sync tracked operations back to Redis.
/// The "Reaper" cleans up (reaps) pending operations from the backing store and syncs them to Redis.
/// </summary>
internal sealed partial class RedisReconciliationReaper : BackgroundService
{
    private readonly IRedisReconciliationService _reconciliationService;
    private readonly ILogger<RedisReconciliationReaper> _logger;
    private readonly RedisReconciliationReaperOptions _options;
    private readonly TimeProvider _timeProvider;

    public RedisReconciliationReaper(
        IRedisReconciliationService reconciliationService,
        IOptionsMonitor<RedisReconciliationReaperOptions> options,
        ILogger<RedisReconciliationReaper> logger,
        TimeProvider timeProvider)
    {
        _reconciliationService = reconciliationService;
        _options = options.CurrentValue;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            LogReaperDisabled(_logger);
            return;
        }

        LogReaperStarted(_logger, _options.Interval, _options.InitialDelay);

        // Initial delay before first reconciliation run
        if (_options.InitialDelay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(_options.InitialDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogReaperStoppedDuringInitialDelay(_logger);
                return;
            }
        }

        // Main reconciliation loop
        while (!stoppingToken.IsCancellationRequested)
        {
            var start = _timeProvider.GetUtcNow();

            try
            {
                var pendingBefore = _reconciliationService.PendingOperations;
                if (pendingBefore > 0)
                {
                    LogReaperRunStarting(_logger, pendingBefore);
                }

                // Always attempt reconciliation so persisted operations can be discovered after restart.
                await _reconciliationService.ReconcileAsync(stoppingToken).ConfigureAwait(false);

                var pendingAfter = _reconciliationService.PendingOperations;
                if (pendingBefore > 0 || pendingAfter > 0)
                {
                    var processed = Math.Max(0, pendingBefore - pendingAfter);
                    LogReaperRunCompleted(_logger, processed, pendingAfter);
                }
                else
                {
                    LogReaperNoPending(_logger);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                LogReaperStoppingGracefully(_logger);
                break;
            }
            catch (Exception ex)
            {
                LogReaperRunError(_logger, ex);
            }

            // Wait for next interval
            var elapsed = _timeProvider.GetUtcNow() - start;
            var delay = _options.Interval - elapsed;

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    LogReaperStoppedDuringIntervalDelay(_logger);
                    break;
                }
            }
        }

        LogReaperStopped(_logger);
    }

    [LoggerMessage(
        EventId = 21000,
        Level = LogLevel.Information,
        Message = "RedisReconciliationReaper is disabled. Skipping background reconciliation.")]
    private static partial void LogReaperDisabled(ILogger logger);

    [LoggerMessage(
        EventId = 21001,
        Level = LogLevel.Information,
        Message = "RedisReconciliationReaper started. Interval: {Interval}, InitialDelay: {InitialDelay}")]
    private static partial void LogReaperStarted(ILogger logger, TimeSpan interval, TimeSpan initialDelay);

    [LoggerMessage(
        EventId = 21002,
        Level = LogLevel.Information,
        Message = "RedisReconciliationReaper stopped during initial delay.")]
    private static partial void LogReaperStoppedDuringInitialDelay(ILogger logger);

    [LoggerMessage(
        EventId = 21003,
        Level = LogLevel.Information,
        Message = "RedisReconciliationReaper: Starting reconciliation run. Pending operations: {Pending}")]
    private static partial void LogReaperRunStarting(ILogger logger, int pending);

    [LoggerMessage(
        EventId = 21004,
        Level = LogLevel.Information,
        Message = "RedisReconciliationReaper: Reconciliation run completed. Processed: {Processed}, Remaining: {Remaining}")]
    private static partial void LogReaperRunCompleted(ILogger logger, int processed, int remaining);

    [LoggerMessage(
        EventId = 21005,
        Level = LogLevel.Debug,
        Message = "RedisReconciliationReaper: No pending operations to reconcile.")]
    private static partial void LogReaperNoPending(ILogger logger);

    [LoggerMessage(
        EventId = 21006,
        Level = LogLevel.Information,
        Message = "RedisReconciliationReaper: Stopping gracefully.")]
    private static partial void LogReaperStoppingGracefully(ILogger logger);

    [LoggerMessage(
        EventId = 21007,
        Level = LogLevel.Error,
        Message = "RedisReconciliationReaper: Error during reconciliation run. Will retry on next interval.")]
    private static partial void LogReaperRunError(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 21008,
        Level = LogLevel.Information,
        Message = "RedisReconciliationReaper: Stopped during interval delay.")]
    private static partial void LogReaperStoppedDuringIntervalDelay(ILogger logger);

    [LoggerMessage(
        EventId = 21009,
        Level = LogLevel.Information,
        Message = "RedisReconciliationReaper: Stopped.")]
    private static partial void LogReaperStopped(ILogger logger);
}
