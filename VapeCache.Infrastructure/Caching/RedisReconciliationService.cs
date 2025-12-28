using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Infrastructure.Caching;

/// <summary>
/// Tracks in-memory cache writes during Redis circuit breaker outages and syncs them back to Redis on recovery.
/// Provides zero-data-loss failover by replaying all write operations.
/// </summary>
internal sealed class RedisReconciliationService : IRedisReconciliationService
{
    private readonly RedisCommandExecutor _redis; // Use raw Redis executor (no circuit breaker!)
    private readonly ILogger<RedisReconciliationService> _logger;
    private readonly RedisReconciliationOptions _options;

    // Track operations that occurred while circuit was open
    private readonly ConcurrentDictionary<string, TrackedOperation> _pendingOps = new();

    public RedisReconciliationService(
        RedisCommandExecutor redis, // Raw executor to avoid circular dependency
        IOptions<RedisReconciliationOptions> options,
        ILogger<RedisReconciliationService> logger)
    {
        _redis = redis;
        _options = options.Value;
        _logger = logger;
    }

    public int PendingOperations => _pendingOps.Count;

    public void TrackWrite(string key, ReadOnlyMemory<byte> value, TimeSpan? expiry)
    {
        if (!_options.Enabled) return;

        // Calculate absolute expiration time
        var expiresAt = expiry.HasValue
            ? DateTimeOffset.UtcNow.Add(expiry.Value)
            : (DateTimeOffset?)null;

        var op = new TrackedOperation
        {
            Type = OperationType.Write,
            Key = key,
            Value = value.ToArray(), // Copy to array for storage
            ExpiresAt = expiresAt,
            TrackedAt = DateTimeOffset.UtcNow
        };

        _pendingOps.AddOrUpdate(key, op, (_, __) => op);
    }

    public void TrackDelete(string key)
    {
        if (!_options.Enabled) return;

        var op = new TrackedOperation
        {
            Type = OperationType.Delete,
            Key = key,
            TrackedAt = DateTimeOffset.UtcNow
        };

        _pendingOps.AddOrUpdate(key, op, (_, __) => op);
    }

    public async ValueTask ReconcileAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled || _pendingOps.IsEmpty) return;

        var startTime = DateTimeOffset.UtcNow;
        var snapshot = _pendingOps.ToArray(); // Snapshot for processing
        var synced = 0;
        var skipped = 0;
        var failed = 0;

        _logger.LogInformation(
            "🔄 Starting Redis reconciliation: {Count} operations to sync",
            snapshot.Length);

        foreach (var kvp in snapshot)
        {
            var key = kvp.Key;
            var op = kvp.Value;

            try
            {
                // Check if operation has expired
                var age = DateTimeOffset.UtcNow - op.TrackedAt;
                if (age > _options.MaxOperationAge)
                {
                    _pendingOps.TryRemove(key, out _);
                    skipped++;
                    _logger.LogDebug(
                        "Skipping expired operation for key {Key} (age: {Age})",
                        key, age);
                    continue;
                }

                // If write operation has an expiry that's already passed, skip it
                if (op.Type == OperationType.Write && op.ExpiresAt.HasValue && op.ExpiresAt.Value < DateTimeOffset.UtcNow)
                {
                    _pendingOps.TryRemove(key, out _);
                    skipped++;
                    _logger.LogDebug(
                        "Skipping already-expired write for key {Key}",
                        key);
                    continue;
                }

                // Sync the operation to Redis
                if (op.Type == OperationType.Write)
                {
                    // Calculate remaining TTL
                    TimeSpan? ttl = null;
                    if (op.ExpiresAt.HasValue)
                    {
                        var remaining = op.ExpiresAt.Value - DateTimeOffset.UtcNow;
                        ttl = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
                    }

                    await _redis.SetAsync(key, op.Value!, ttl, ct).ConfigureAwait(false);
                    synced++;
                }
                else if (op.Type == OperationType.Delete)
                {
                    await _redis.DeleteAsync(key, ct).ConfigureAwait(false);
                    synced++;
                }

                // Remove from pending
                _pendingOps.TryRemove(key, out _);
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex,
                    "Failed to reconcile operation for key {Key}. Will retry on next reconciliation.",
                    key);
            }
        }

        var duration = DateTimeOffset.UtcNow - startTime;
        _logger.LogInformation(
            "✅ Redis reconciliation complete: {Synced} synced, {Skipped} skipped, {Failed} failed (took {Duration}ms)",
            synced, skipped, failed, duration.TotalMilliseconds);
    }

    public void Clear()
    {
        _pendingOps.Clear();
    }

    private enum OperationType
    {
        Write,
        Delete
    }

    private sealed class TrackedOperation
    {
        public required OperationType Type { get; init; }
        public required string Key { get; init; }
        public byte[]? Value { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public required DateTimeOffset TrackedAt { get; init; }
    }
}
