namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Defines the cache stats contract.
/// </summary>
public interface ICacheStats
{
    /// <summary>
    /// Gets the snapshot.
    /// </summary>
    CacheStatsSnapshot Snapshot { get; }
}

/// <summary>
/// Represents the struct.
/// </summary>
public readonly record struct CacheStatsSnapshot
{
    public CacheStatsSnapshot(
        long GetCalls,
        long Hits,
        long Misses,
        long SetCalls,
        long RemoveCalls,
        long FallbackToMemory,
        long RedisBreakerOpened,
        long StampedeKeyRejected,
        long StampedeLockWaitTimeout,
        long StampedeFailureBackoffRejected)
    {
        this.GetCalls = GetCalls;
        this.Hits = Hits;
        this.Misses = Misses;
        this.SetCalls = SetCalls;
        this.RemoveCalls = RemoveCalls;
        this.FallbackToMemory = FallbackToMemory;
        this.RedisBreakerOpened = RedisBreakerOpened;
        this.StampedeKeyRejected = StampedeKeyRejected;
        this.StampedeLockWaitTimeout = StampedeLockWaitTimeout;
        this.StampedeFailureBackoffRejected = StampedeFailureBackoffRejected;
    }

    public long GetCalls { get; init; }
    public long Hits { get; init; }
    public long Misses { get; init; }
    public long SetCalls { get; init; }
    public long RemoveCalls { get; init; }
    public long FallbackToMemory { get; init; }
    public long RedisBreakerOpened { get; init; }
    public long StampedeKeyRejected { get; init; }
    public long StampedeLockWaitTimeout { get; init; }
    public long StampedeFailureBackoffRejected { get; init; }
}
