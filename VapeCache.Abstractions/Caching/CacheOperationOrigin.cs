namespace VapeCache.Abstractions.Caching;

public static class CacheOperationOrigin
{
    public const string Native = "native";
    public const string DistributedCacheBridge = "distributed-cache";
}

public interface ICacheOperationOriginAccessor
{
    string CurrentOrigin { get; }
    IDisposable BeginScope(string origin);
}

public readonly record struct CacheOriginStatsSnapshot(
    long NativeReads,
    long NativeWrites,
    long NativeHits,
    long NativeMisses,
    long InteropReads,
    long InteropWrites,
    long InteropHits,
    long InteropMisses);

public interface ICacheOriginStats
{
    CacheOriginStatsSnapshot Snapshot { get; }
}
