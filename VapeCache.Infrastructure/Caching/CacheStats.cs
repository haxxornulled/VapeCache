using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching;

public sealed class CacheStats : ICacheStats
{
    private long _get;
    private long _hit;
    private long _miss;
    private long _set;
    private long _remove;
    private long _fallbackToMemory;
    private long _breakerOpened;
    private long _stampedeKeyRejected;
    private long _stampedeLockWaitTimeout;
    private long _stampedeFailureBackoffRejected;

    public CacheStatsSnapshot Snapshot => new(
        GetCalls: Volatile.Read(ref _get),
        Hits: Volatile.Read(ref _hit),
        Misses: Volatile.Read(ref _miss),
        SetCalls: Volatile.Read(ref _set),
        RemoveCalls: Volatile.Read(ref _remove),
        FallbackToMemory: Volatile.Read(ref _fallbackToMemory),
        RedisBreakerOpened: Volatile.Read(ref _breakerOpened),
        StampedeKeyRejected: Volatile.Read(ref _stampedeKeyRejected),
        StampedeLockWaitTimeout: Volatile.Read(ref _stampedeLockWaitTimeout),
        StampedeFailureBackoffRejected: Volatile.Read(ref _stampedeFailureBackoffRejected));

    /// <summary>
    /// Executes value.
    /// </summary>
    public void IncGet() => Interlocked.Increment(ref _get);
    /// <summary>
    /// Executes value.
    /// </summary>
    public void IncHit() => Interlocked.Increment(ref _hit);
    /// <summary>
    /// Executes value.
    /// </summary>
    public void IncMiss() => Interlocked.Increment(ref _miss);
    /// <summary>
    /// Executes value.
    /// </summary>
    public void IncSet() => Interlocked.Increment(ref _set);
    /// <summary>
    /// Executes value.
    /// </summary>
    public void IncRemove() => Interlocked.Increment(ref _remove);
    /// <summary>
    /// Executes value.
    /// </summary>
    public void IncFallbackToMemory() => Interlocked.Increment(ref _fallbackToMemory);
    /// <summary>
    /// Executes value.
    /// </summary>
    public void IncBreakerOpened() => Interlocked.Increment(ref _breakerOpened);
    /// <summary>
    /// Executes value.
    /// </summary>
    public void IncStampedeKeyRejected() => Interlocked.Increment(ref _stampedeKeyRejected);
    /// <summary>
    /// Executes value.
    /// </summary>
    public void IncStampedeLockWaitTimeout() => Interlocked.Increment(ref _stampedeLockWaitTimeout);
    /// <summary>
    /// Executes value.
    /// </summary>
    public void IncStampedeFailureBackoffRejected() => Interlocked.Increment(ref _stampedeFailureBackoffRejected);
}
