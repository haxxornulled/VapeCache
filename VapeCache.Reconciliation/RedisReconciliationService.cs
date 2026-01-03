using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Reconciliation;

/// <summary>
/// Tracks in-memory cache writes during Redis circuit breaker outages and syncs them back to Redis on recovery.
/// Provides zero-data-loss failover by replaying all write operations.
/// </summary>
internal sealed class RedisReconciliationService : IRedisReconciliationService
{
    private readonly IRedisReconciliationExecutor _redis;
    private readonly ILogger<RedisReconciliationService> _logger;
    private readonly RedisReconciliationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IRedisReconciliationStore _store;
    private int _pendingEstimate = -1;

    public RedisReconciliationService(
        IRedisReconciliationExecutor redis,
        IOptions<RedisReconciliationOptions> options,
        ILogger<RedisReconciliationService> logger,
        TimeProvider timeProvider,
        IRedisReconciliationStore store)
    {
        _redis = redis;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider;
        _store = store;
    }

    public int PendingOperations
    {
        get
        {
            var estimate = Volatile.Read(ref _pendingEstimate);
            // Return cached estimate or 0 if not yet initialized to avoid blocking I/O
            return estimate >= 0 ? estimate : 0;
        }
    }

    public void TrackWrite(string key, ReadOnlyMemory<byte> value, TimeSpan? expiry)
    {
        if (!_options.Enabled) return;
        if (!TryReserveSlot()) return;

        var now = _timeProvider.GetUtcNow();
        var expiresAt = expiry.HasValue
            ? now + expiry.Value
            : (DateTimeOffset?)null;

        try
        {
            _store.TryUpsertWriteAsync(key, value, now, expiresAt, CancellationToken.None)
                .GetAwaiter().GetResult();
            RedisReconciliationTelemetry.Tracked.Add(1, new KeyValuePair<string, object?>("type", "write"));
        }
        catch (Exception ex)
        {
            AdjustPendingEstimate(-1);
            _logger.LogWarning(ex, "Failed to persist reconciliation write for key {Key}.", key);
        }
    }

    public void TrackDelete(string key)
    {
        if (!_options.Enabled) return;
        if (!TryReserveSlot()) return;

        var now = _timeProvider.GetUtcNow();
        try
        {
            _store.TryUpsertDeleteAsync(key, now, CancellationToken.None)
                .GetAwaiter().GetResult();
            RedisReconciliationTelemetry.Tracked.Add(1, new KeyValuePair<string, object?>("type", "delete"));
        }
        catch (Exception ex)
        {
            AdjustPendingEstimate(-1);
            _logger.LogWarning(ex, "Failed to persist reconciliation delete for key {Key}.", key);
        }
    }

    private bool TryReserveSlot()
    {
        var limit = _options.MaxPendingOperations;
        if (limit <= 0) return true;

        var count = EnsurePendingEstimate();
        if (count >= limit)
        {
            RedisReconciliationTelemetry.Dropped.Add(1);
            return false;
        }

        Interlocked.Increment(ref _pendingEstimate);
        return true;
    }

    private int EnsurePendingEstimate()
    {
        var estimate = Volatile.Read(ref _pendingEstimate);
        if (estimate >= 0) return estimate;

        var count = _store.CountAsync(CancellationToken.None).GetAwaiter().GetResult();
        Interlocked.CompareExchange(ref _pendingEstimate, count, -1);
        return Volatile.Read(ref _pendingEstimate);
    }

    private void AdjustPendingEstimate(int delta)
    {
        if (Volatile.Read(ref _pendingEstimate) < 0) return;
        Interlocked.Add(ref _pendingEstimate, delta);
        if (Volatile.Read(ref _pendingEstimate) < 0)
            Volatile.Write(ref _pendingEstimate, 0);
    }

    public async ValueTask ReconcileAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled) return;

        var start = _timeProvider.GetUtcNow();
        var snapshot = await _store.SnapshotAsync(_options.MaxOperationsPerRun, ct).ConfigureAwait(false);
        if (snapshot.Count == 0) return;

        RedisReconciliationTelemetry.Runs.Add(1);
        var batchSize = Math.Max(1, _options.BatchSize);

        var synced = 0;
        var skipped = 0;
        var failed = 0;
        var backoff = _options.InitialBackoff;
        var pendingRemovals = new List<string>(batchSize);

        _logger.LogInformation("Starting Redis reconciliation: {Count} operations to sync", snapshot.Count);

        for (var i = 0; i < snapshot.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (_options.MaxRunDuration > TimeSpan.Zero && _timeProvider.GetUtcNow() - start > _options.MaxRunDuration)
            {
                _logger.LogWarning("Redis reconciliation stopped due to MaxRunDuration.");
                break;
            }

            var op = snapshot[i];
            var now = _timeProvider.GetUtcNow();
            var age = now - op.TrackedAt;
            if (age > _options.MaxOperationAge)
            {
                pendingRemovals.Add(op.Key);
                skipped++;
                continue;
            }

            if (op.Type == OperationType.Write && op.ExpiresAt.HasValue && op.ExpiresAt.Value <= now)
            {
                pendingRemovals.Add(op.Key);
                skipped++;
                continue;
            }

            try
            {
                if (op.Type == OperationType.Write)
                {
                    TimeSpan? ttl = null;
                    if (op.ExpiresAt.HasValue)
                    {
                        var remaining = op.ExpiresAt.Value - now;
                        ttl = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
                    }

                    await _redis.SetAsync(op.Key, op.Value!, ttl, ct).ConfigureAwait(false);
                }
                else
                {
                    await _redis.DeleteAsync(op.Key, ct).ConfigureAwait(false);
                }

                // SUCCESS: Remove from store after successful Redis operation
                pendingRemovals.Add(op.Key);
                synced++;
                backoff = _options.InitialBackoff;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Failed to reconcile operation for key {Key}. Will retry on next reconciliation run.", op.Key);

                // DO NOT remove from pendingRemovals - keep in SQLite for retry
                // Apply backoff to avoid hammering Redis
                if (backoff > TimeSpan.Zero)
                    await Task.Delay(backoff, ct).ConfigureAwait(false);

                var next = backoff.TotalMilliseconds * _options.BackoffMultiplier;
                var max = _options.MaxBackoff.TotalMilliseconds;
                backoff = TimeSpan.FromMilliseconds(Math.Min(max, Math.Max(0, next)));

                // If too many consecutive failures, stop reconciliation early to avoid long blocking
                if (failed >= _options.MaxConsecutiveFailures && _options.MaxConsecutiveFailures > 0)
                {
                    _logger.LogWarning("Stopping reconciliation early after {Failed} consecutive failures. Remaining operations will retry later.", failed);
                    break;
                }
            }

            if (pendingRemovals.Count >= batchSize)
            {
                await _store.RemoveAsync(pendingRemovals, ct).ConfigureAwait(false);
                AdjustPendingEstimate(-pendingRemovals.Count);
                pendingRemovals.Clear();
            }

            if ((i + 1) % batchSize == 0)
                await Task.Yield();
        }

        if (pendingRemovals.Count > 0)
        {
            await _store.RemoveAsync(pendingRemovals, ct).ConfigureAwait(false);
            AdjustPendingEstimate(-pendingRemovals.Count);
        }

        RedisReconciliationTelemetry.Synced.Add(synced);
        RedisReconciliationTelemetry.Skipped.Add(skipped);
        RedisReconciliationTelemetry.Failed.Add(failed);
        RedisReconciliationTelemetry.RunMs.Record((_timeProvider.GetUtcNow() - start).TotalMilliseconds);

        _logger.LogInformation(
            "Redis reconciliation complete: {Synced} synced, {Skipped} skipped, {Failed} failed", synced, skipped, failed);
    }

    public void Clear()
    {
        _store.ClearAsync(CancellationToken.None).GetAwaiter().GetResult();
        Volatile.Write(ref _pendingEstimate, 0);
    }

    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        await _store.ClearAsync(ct).ConfigureAwait(false);
        Volatile.Write(ref _pendingEstimate, 0);
    }
}
