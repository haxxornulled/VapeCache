using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Reconciliation;

/// <summary>
/// Background service that periodically runs reconciliation to sync tracked operations back to Redis.
/// The "Reaper" cleans up (reaps) pending operations from the backing store and syncs them to Redis.
/// </summary>
internal sealed class RedisReconciliationReaper : BackgroundService
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
            _logger.LogInformation("RedisReconciliationReaper is disabled. Skipping background reconciliation.");
            return;
        }

        _logger.LogInformation(
            "RedisReconciliationReaper started. Interval: {Interval}, InitialDelay: {InitialDelay}",
            _options.Interval,
            _options.InitialDelay);

        // Initial delay before first reconciliation run
        if (_options.InitialDelay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(_options.InitialDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("RedisReconciliationReaper stopped during initial delay.");
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
                    _logger.LogInformation(
                        "RedisReconciliationReaper: Starting reconciliation run. Pending operations: {Pending}",
                        pendingBefore);

                    await _reconciliationService.ReconcileAsync(stoppingToken).ConfigureAwait(false);

                    var pendingAfter = _reconciliationService.PendingOperations;
                    var processed = pendingBefore - pendingAfter;

                    _logger.LogInformation(
                        "RedisReconciliationReaper: Reconciliation run completed. Processed: {Processed}, Remaining: {Remaining}",
                        processed,
                        pendingAfter);
                }
                else
                {
                    _logger.LogDebug("RedisReconciliationReaper: No pending operations to reconcile.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("RedisReconciliationReaper: Stopping gracefully.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RedisReconciliationReaper: Error during reconciliation run. Will retry on next interval.");
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
                    _logger.LogInformation("RedisReconciliationReaper: Stopped during interval delay.");
                    break;
                }
            }
        }

        _logger.LogInformation("RedisReconciliationReaper: Stopped.");
    }
}
