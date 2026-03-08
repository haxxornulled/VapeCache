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
public readonly record struct CacheStatsSnapshot(
    long GetCalls,
    long Hits,
    long Misses,
    long SetCalls,
    long RemoveCalls,
    long FallbackToMemory,
    long RedisBreakerOpened,
    long StampedeKeyRejected,
    long StampedeLockWaitTimeout,
    long StampedeFailureBackoffRejected);
