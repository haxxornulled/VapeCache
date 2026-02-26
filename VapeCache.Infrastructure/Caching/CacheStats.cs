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

    public void IncGet() => Interlocked.Increment(ref _get);
    public void IncHit() => Interlocked.Increment(ref _hit);
    public void IncMiss() => Interlocked.Increment(ref _miss);
    public void IncSet() => Interlocked.Increment(ref _set);
    public void IncRemove() => Interlocked.Increment(ref _remove);
    public void IncFallbackToMemory() => Interlocked.Increment(ref _fallbackToMemory);
    public void IncBreakerOpened() => Interlocked.Increment(ref _breakerOpened);
    public void IncStampedeKeyRejected() => Interlocked.Increment(ref _stampedeKeyRejected);
    public void IncStampedeLockWaitTimeout() => Interlocked.Increment(ref _stampedeLockWaitTimeout);
    public void IncStampedeFailureBackoffRejected() => Interlocked.Increment(ref _stampedeFailureBackoffRejected);
}
