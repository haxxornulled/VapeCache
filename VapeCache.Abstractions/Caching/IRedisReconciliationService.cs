namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Tracks in-memory cache writes during Redis outages and syncs them back when Redis recovers.
/// This ensures zero data loss during circuit breaker failover scenarios.
/// </summary>
public interface IRedisReconciliationService
{
    /// <summary>
    /// Records a write operation that occurred while circuit was open.
    /// </summary>
    void TrackWrite(string key, ReadOnlyMemory<byte> value, TimeSpan? expiry);

    /// <summary>
    /// Records a delete operation that occurred while circuit was open.
    /// </summary>
    void TrackDelete(string key);

    /// <summary>
    /// Syncs all tracked operations to Redis. Called when circuit transitions to closed.
    /// </summary>
    ValueTask ReconcileAsync(CancellationToken ct = default);

    /// <summary>
    /// Number of pending operations waiting to be synced.
    /// </summary>
    int PendingOperations { get; }

    /// <summary>
    /// Clears all tracked operations (used when reconciliation completes or is abandoned).
    /// </summary>
    void Clear();

    /// <summary>
    /// Clears any persisted reconciliation state (backing store flush).
    /// </summary>
    ValueTask FlushAsync(CancellationToken ct = default);
}
