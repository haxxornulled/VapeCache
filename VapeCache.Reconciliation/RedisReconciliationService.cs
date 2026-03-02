using System.Collections.Concurrent;
using System.Threading.Channels;
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
    private readonly Channel<TrackingWorkItem>? _trackingQueue;
    private readonly Task? _trackingPumpTask;
    private readonly ConcurrentDictionary<string, int> _queuedKeyCounts = new(StringComparer.Ordinal);
    private int _pendingEstimate = -1;
    private int _queuedOperations;
    private int _queuedDistinctKeys;
    private int _queueFullWarningCount;

    public RedisReconciliationService(
        IRedisReconciliationExecutor redis,
        IOptionsMonitor<RedisReconciliationOptions> options,
        ILogger<RedisReconciliationService> logger,
        TimeProvider timeProvider,
        IRedisReconciliationStore store)
    {
        _redis = redis;
        _options = options.CurrentValue;
        _logger = logger;
        _timeProvider = timeProvider;
        _store = store;
        if (_options.Enabled)
        {
            _trackingQueue = CreateTrackingQueue(_options);
            _trackingPumpTask = Task.Run(ProcessTrackingQueueAsync);
        }
    }

    public int PendingOperations
    {
        get
        {
            var estimate = Volatile.Read(ref _pendingEstimate);
            if (estimate < 0)
                estimate = 0;

            var queued = Volatile.Read(ref _queuedDistinctKeys);
            if (queued < 0)
                queued = 0;

            return estimate + queued;
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public void TrackWrite(string key, ReadOnlyMemory<byte> value, TimeSpan? expiry)
    {
        if (!_options.Enabled) return;
        if (!TryReserveSlot()) return;

        var now = _timeProvider.GetUtcNow();
        var expiresAt = expiry.HasValue
            ? now + expiry.Value
            : (DateTimeOffset?)null;

        var mutation = new TrackingMutation(OperationType.Write, key, value.ToArray(), now, expiresAt);
        if (!TryQueueMutation(mutation, "write"))
            return;

        RedisReconciliationTelemetry.Tracked.Add(1, new KeyValuePair<string, object?>("type", "write"));
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public void TrackDelete(string key)
    {
        if (!_options.Enabled) return;
        if (!TryReserveSlot()) return;

        var now = _timeProvider.GetUtcNow();
        var mutation = new TrackingMutation(OperationType.Delete, key, null, now, null);
        if (!TryQueueMutation(mutation, "delete"))
            return;

        RedisReconciliationTelemetry.Tracked.Add(1, new KeyValuePair<string, object?>("type", "delete"));
    }

    private bool TryReserveSlot()
    {
        var limit = _options.MaxPendingOperations;
        if (limit <= 0) return true;

        var estimate = Volatile.Read(ref _pendingEstimate);
        if (estimate < 0)
            estimate = 0;

        var queuedDistinct = Volatile.Read(ref _queuedDistinctKeys);
        if (queuedDistinct < 0)
            queuedDistinct = 0;

        if (Math.Max(estimate, queuedDistinct) >= limit)
        {
            RedisReconciliationTelemetry.Dropped.Add(1);
            return false;
        }

        return true;
    }

    private void AdjustPendingEstimate(int delta)
    {
        if (Volatile.Read(ref _pendingEstimate) < 0) return;
        Interlocked.Add(ref _pendingEstimate, delta);
        if (Volatile.Read(ref _pendingEstimate) < 0)
            Volatile.Write(ref _pendingEstimate, 0);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask ReconcileAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled) return;

        await DrainTrackingQueueAsync(ct).ConfigureAwait(false);
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

    /// <summary>
    /// Executes value.
    /// </summary>
    public void Clear()
    {
        FlushAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        await DrainTrackingQueueAsync(ct).ConfigureAwait(false);
        await _store.ClearAsync(ct).ConfigureAwait(false);
        Volatile.Write(ref _pendingEstimate, 0);
    }

    private static Channel<TrackingWorkItem> CreateTrackingQueue(RedisReconciliationOptions options)
    {
        var limit = options.MaxPendingOperations;
        if (limit > 0)
        {
            return Channel.CreateBounded<TrackingWorkItem>(new BoundedChannelOptions(limit)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
        }

        return Channel.CreateUnbounded<TrackingWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    private bool TryQueueMutation(TrackingMutation mutation, string mutationType)
    {
        if (_trackingQueue is null || !_trackingQueue.Writer.TryWrite(new MutationWorkItem(mutation)))
        {
            RedisReconciliationTelemetry.Dropped.Add(1, new KeyValuePair<string, object?>("type", mutationType));
            var warningCount = Interlocked.Increment(ref _queueFullWarningCount);
            if (warningCount <= 3 || warningCount % 100 == 0)
            {
                _logger.LogWarning(
                    "Reconciliation tracking queue is full; dropping {MutationType} for key {Key}. PendingOperations={PendingOperations}",
                    mutationType,
                    mutation.Key,
                    PendingOperations);
            }

            return false;
        }

        Interlocked.Increment(ref _queuedOperations);
        RegisterQueuedKey(mutation.Key);
        return true;
    }

    private async Task ProcessTrackingQueueAsync()
    {
        if (_trackingQueue is null)
            return;

        await RefreshPendingEstimateAsync(CancellationToken.None).ConfigureAwait(false);

        var batchSize = Math.Max(1, _options.BatchSize);
        var pending = new List<TrackingMutation>(batchSize);
        try
        {
            while (await _trackingQueue.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                while (_trackingQueue.Reader.TryRead(out var workItem))
                {
                    switch (workItem)
                    {
                        case MutationWorkItem mutationWork:
                            pending.Add(mutationWork.Mutation);
                            if (pending.Count >= batchSize)
                                await PersistPendingMutationsAsync(pending, CancellationToken.None).ConfigureAwait(false);
                            break;

                        case FlushWorkItem flushWork:
                            try
                            {
                                await PersistPendingMutationsAsync(pending, CancellationToken.None).ConfigureAwait(false);
                                flushWork.Completion.TrySetResult(true);
                            }
                            catch (Exception ex)
                            {
                                flushWork.Completion.TrySetException(ex);
                            }
                            break;
                    }
                }

                if (pending.Count > 0)
                    await PersistPendingMutationsAsync(pending, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reconciliation tracking queue pump stopped unexpectedly.");
        }
        finally
        {
            while (_trackingQueue.Reader.TryRead(out var workItem))
            {
                switch (workItem)
                {
                    case MutationWorkItem mutationWork:
                        pending.Add(mutationWork.Mutation);
                        break;
                    case FlushWorkItem flushWork:
                        flushWork.Completion.TrySetCanceled();
                        break;
                }
            }

            if (pending.Count > 0)
                await PersistPendingMutationsAsync(pending, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async ValueTask PersistPendingMutationsAsync(List<TrackingMutation> pending, CancellationToken ct)
    {
        if (pending.Count == 0)
            return;

        foreach (var mutation in pending)
        {
            try
            {
                var inserted = mutation.Type switch
                {
                    OperationType.Write => await _store.TryUpsertWriteAsync(
                        mutation.Key,
                        mutation.Value!,
                        mutation.TrackedAt,
                        mutation.ExpiresAt,
                        ct).ConfigureAwait(false),
                    OperationType.Delete => await _store.TryUpsertDeleteAsync(
                        mutation.Key,
                        mutation.TrackedAt,
                        ct).ConfigureAwait(false),
                    _ => false
                };

                if (inserted)
                    AdjustPendingEstimate(1);
            }
            catch (Exception ex)
            {
                RedisReconciliationTelemetry.Dropped.Add(1, new KeyValuePair<string, object?>("type", mutation.Type.ToString().ToLowerInvariant()));
                _logger.LogWarning(
                    ex,
                    "Failed to persist reconciliation {MutationType} for key {Key}.",
                    mutation.Type,
                    mutation.Key);
            }
            finally
            {
                UnregisterQueuedKey(mutation.Key);
                Interlocked.Decrement(ref _queuedOperations);
            }
        }

        pending.Clear();
    }

    private async ValueTask RefreshPendingEstimateAsync(CancellationToken ct)
    {
        var estimate = Volatile.Read(ref _pendingEstimate);
        if (estimate >= 0)
            return;

        try
        {
            var count = await _store.CountAsync(ct).ConfigureAwait(false);
            Interlocked.CompareExchange(ref _pendingEstimate, count, -1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to load reconciliation pending estimate asynchronously. Starting from zero.");
            Interlocked.CompareExchange(ref _pendingEstimate, 0, -1);
        }
    }

    private async ValueTask DrainTrackingQueueAsync(CancellationToken ct)
    {
        if (!_options.Enabled || _trackingQueue is null)
            return;

        if (_trackingPumpTask is { IsCompleted: true })
            await _trackingPumpTask.ConfigureAwait(false);

        if (Volatile.Read(ref _queuedOperations) == 0)
            return;

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _trackingQueue.Writer.WriteAsync(new FlushWorkItem(completion), ct).ConfigureAwait(false);
        await completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private void RegisterQueuedKey(string key)
    {
        var count = _queuedKeyCounts.AddOrUpdate(key, 1, static (_, existing) => existing + 1);
        if (count == 1)
            Interlocked.Increment(ref _queuedDistinctKeys);
    }

    private void UnregisterQueuedKey(string key)
    {
        while (true)
        {
            if (!_queuedKeyCounts.TryGetValue(key, out var current))
                return;

            if (current <= 1)
            {
                if (_queuedKeyCounts.TryRemove(key, out _))
                {
                    Interlocked.Decrement(ref _queuedDistinctKeys);
                    return;
                }

                continue;
            }

            if (_queuedKeyCounts.TryUpdate(key, current - 1, current))
                return;
        }
    }

    private abstract record TrackingWorkItem;

    private sealed record MutationWorkItem(TrackingMutation Mutation) : TrackingWorkItem;

    private sealed record FlushWorkItem(TaskCompletionSource<bool> Completion) : TrackingWorkItem;

    private sealed record TrackingMutation(
        OperationType Type,
        string Key,
        byte[]? Value,
        DateTimeOffset TrackedAt,
        DateTimeOffset? ExpiresAt);
}
