using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Reconciliation;

/// <summary>
/// Tracks in-memory cache writes during Redis circuit breaker outages and syncs them back to Redis on recovery.
/// Provides no-drop tracking semantics by replaying tracked write/delete operations.
/// </summary>
internal sealed partial class RedisReconciliationService : IRedisReconciliationService
{
    private readonly IRedisReconciliationExecutor _redis;
    private readonly ILogger<RedisReconciliationService> _logger;
    private readonly RedisReconciliationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IRedisReconciliationStore _store;
    private readonly Channel<TrackingWorkItem>? _trackingQueue;
    private readonly Task? _trackingPumpTask;
    private readonly ConcurrentDictionary<string, TrackingMutation> _deferredMutations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _queuedKeyCounts = new(StringComparer.Ordinal);
    private int _pendingEstimate = -1;
    private int _queuedOperations;
    private int _queuedDistinctKeys;
    private int _thresholdWarningCount;
    private int _queueFallbackWarningCount;
    private int _deferredPersistWarningCount;

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
            _trackingQueue = CreateTrackingQueue();
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

            return estimate + queued + _deferredMutations.Count;
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public void TrackWrite(string key, ReadOnlyMemory<byte> value, TimeSpan? expiry)
    {
        if (!_options.Enabled) return;
        LogIfPendingThresholdExceeded();

        var now = _timeProvider.GetUtcNow();
        var expiresAt = expiry.HasValue
            ? now + expiry.Value
            : (DateTimeOffset?)null;

        var mutation = new TrackingMutation(OperationType.Write, key, value.ToArray(), now, expiresAt);
        QueueMutationNoDrop(mutation, "write");

        RedisReconciliationTelemetry.Tracked.Add(1, new KeyValuePair<string, object?>("type", "write"));
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public void TrackDelete(string key)
    {
        if (!_options.Enabled) return;
        LogIfPendingThresholdExceeded();

        var now = _timeProvider.GetUtcNow();
        var mutation = new TrackingMutation(OperationType.Delete, key, null, now, null);
        QueueMutationNoDrop(mutation, "delete");

        RedisReconciliationTelemetry.Tracked.Add(1, new KeyValuePair<string, object?>("type", "delete"));
    }

    private void LogIfPendingThresholdExceeded()
    {
        var limit = _options.MaxPendingOperations;
        if (limit <= 0)
            return;

        var pending = PendingOperations;
        if (pending >= limit)
        {
            var warningCount = Interlocked.Increment(ref _thresholdWarningCount);
            if (warningCount <= 3 || warningCount % 100 == 0)
            {
                LogPendingThresholdExceeded(_logger, limit, pending);
            }
        }
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
        await FlushDeferredMutationsAsync(ct).ConfigureAwait(false);
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

        LogReconciliationStarting(_logger, snapshot.Count);

        for (var i = 0; i < snapshot.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (_options.MaxRunDuration > TimeSpan.Zero && _timeProvider.GetUtcNow() - start > _options.MaxRunDuration)
            {
                LogReconciliationStoppedMaxDuration(_logger);
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
                LogReconciliationOperationFailed(_logger, ex, op.Key);

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
                    LogReconciliationStoppedConsecutiveFailures(_logger, failed);
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

        LogReconciliationComplete(_logger, synced, skipped, failed);
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
        _deferredMutations.Clear();
        await _store.ClearAsync(ct).ConfigureAwait(false);
        Volatile.Write(ref _pendingEstimate, 0);
    }

    private static Channel<TrackingWorkItem> CreateTrackingQueue()
    {
        return Channel.CreateUnbounded<TrackingWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    private void QueueMutationNoDrop(TrackingMutation mutation, string mutationType)
    {
        if (_trackingQueue is not null &&
            _trackingPumpTask is not { IsCompleted: true } &&
            _trackingQueue.Writer.TryWrite(new MutationWorkItem(mutation)))
        {
            Interlocked.Increment(ref _queuedOperations);
            RegisterQueuedKey(mutation.Key);
            return;
        }

        PersistMutationInlineNoDrop(mutation, mutationType);
    }

    private void PersistMutationInlineNoDrop(TrackingMutation mutation, string mutationType)
    {
        try
        {
            PersistSingleMutationAsync(mutation, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            return;
        }
        catch (Exception ex)
        {
            _deferredMutations[mutation.Key] = mutation;
            var warningCount = Interlocked.Increment(ref _queueFallbackWarningCount);
            if (warningCount <= 3 || warningCount % 100 == 0)
            {
                LogInlinePersistenceFailed(_logger, ex, mutationType, mutation.Key, PendingOperations);
            }
        }
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
            LogTrackingQueuePumpStopped(_logger, ex);
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
                await PersistSingleMutationAsync(mutation, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _deferredMutations[mutation.Key] = mutation;
                var warningCount = Interlocked.Increment(ref _deferredPersistWarningCount);
                LogDeferredPersistFailed(_logger, ex, mutation.Type, mutation.Key);
                if (warningCount <= 3 || warningCount % 100 == 0)
                {
                    LogDeferredBufferSize(_logger, _deferredMutations.Count);
                }
            }
            finally
            {
                UnregisterQueuedKey(mutation.Key);
                Interlocked.Decrement(ref _queuedOperations);
            }
        }

        pending.Clear();
    }

    private async ValueTask PersistSingleMutationAsync(TrackingMutation mutation, CancellationToken ct)
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

    private async ValueTask FlushDeferredMutationsAsync(CancellationToken ct)
    {
        if (_deferredMutations.IsEmpty)
            return;

        foreach (var kvp in _deferredMutations.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await PersistSingleMutationAsync(kvp.Value, ct).ConfigureAwait(false);
                _deferredMutations.TryRemove(kvp.Key, out _);
            }
            catch (Exception ex)
            {
                LogDeferredPersistenceRetry(_logger, ex, kvp.Key);
            }
        }
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
            LogPendingEstimateLoadFailed(_logger, ex);
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

    [LoggerMessage(
        EventId = 22000,
        Level = LogLevel.Warning,
        Message = "Reconciliation pending operations exceeded advisory threshold {Threshold}. PendingOperations={PendingOperations}. Tracking continues to preserve no-drop guarantees.")]
    private static partial void LogPendingThresholdExceeded(ILogger logger, int threshold, int pendingOperations);

    [LoggerMessage(
        EventId = 22001,
        Level = LogLevel.Information,
        Message = "Starting Redis reconciliation: {Count} operations to sync")]
    private static partial void LogReconciliationStarting(ILogger logger, int count);

    [LoggerMessage(
        EventId = 22002,
        Level = LogLevel.Warning,
        Message = "Redis reconciliation stopped due to MaxRunDuration.")]
    private static partial void LogReconciliationStoppedMaxDuration(ILogger logger);

    [LoggerMessage(
        EventId = 22003,
        Level = LogLevel.Warning,
        Message = "Failed to reconcile operation for key {Key}. Will retry on next reconciliation run.")]
    private static partial void LogReconciliationOperationFailed(ILogger logger, Exception exception, string key);

    [LoggerMessage(
        EventId = 22004,
        Level = LogLevel.Warning,
        Message = "Stopping reconciliation early after {Failed} consecutive failures. Remaining operations will retry later.")]
    private static partial void LogReconciliationStoppedConsecutiveFailures(ILogger logger, int failed);

    [LoggerMessage(
        EventId = 22005,
        Level = LogLevel.Information,
        Message = "Redis reconciliation complete: {Synced} synced, {Skipped} skipped, {Failed} failed")]
    private static partial void LogReconciliationComplete(ILogger logger, int synced, int skipped, int failed);

    [LoggerMessage(
        EventId = 22006,
        Level = LogLevel.Warning,
        Message = "Reconciliation inline persistence failed for {MutationType} key {Key}; queued for retry. PendingOperations={PendingOperations}")]
    private static partial void LogInlinePersistenceFailed(
        ILogger logger,
        Exception exception,
        string mutationType,
        string key,
        int pendingOperations);

    [LoggerMessage(
        EventId = 22007,
        Level = LogLevel.Warning,
        Message = "Reconciliation tracking queue pump stopped unexpectedly.")]
    private static partial void LogTrackingQueuePumpStopped(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 22008,
        Level = LogLevel.Warning,
        Message = "Failed to persist reconciliation {MutationType} for key {Key}; queued for retry.")]
    private static partial void LogDeferredPersistFailed(
        ILogger logger,
        Exception exception,
        OperationType mutationType,
        string key);

    [LoggerMessage(
        EventId = 22009,
        Level = LogLevel.Warning,
        Message = "Deferred reconciliation buffer contains {DeferredCount} keys after persistence failure.")]
    private static partial void LogDeferredBufferSize(ILogger logger, int deferredCount);

    [LoggerMessage(
        EventId = 22010,
        Level = LogLevel.Warning,
        Message = "Deferred reconciliation persistence failed for key {Key}; will retry on next run.")]
    private static partial void LogDeferredPersistenceRetry(ILogger logger, Exception exception, string key);

    [LoggerMessage(
        EventId = 22011,
        Level = LogLevel.Warning,
        Message = "Failed to load reconciliation pending estimate asynchronously. Starting from zero.")]
    private static partial void LogPendingEstimateLoadFailed(ILogger logger, Exception exception);

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
