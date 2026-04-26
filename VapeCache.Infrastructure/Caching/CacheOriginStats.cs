using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching;

internal sealed class CacheOriginStats : ICacheOriginStats
{
    private readonly ICacheOperationOriginAccessor _originAccessor;
    private readonly CacheStats _native = new();
    private readonly CacheStats _interop = new();

    public CacheOriginStats(ICacheOperationOriginAccessor originAccessor)
    {
        _originAccessor = originAccessor;
    }

    public CacheOriginStatsSnapshot Snapshot
    {
        get
        {
            var native = _native.Snapshot;
            var interop = _interop.Snapshot;
            return new CacheOriginStatsSnapshot(
                NativeReads: native.GetCalls,
                NativeWrites: native.SetCalls + native.RemoveCalls,
                NativeHits: native.Hits,
                NativeMisses: native.Misses,
                InteropReads: interop.GetCalls,
                InteropWrites: interop.SetCalls + interop.RemoveCalls,
                InteropHits: interop.Hits,
                InteropMisses: interop.Misses);
        }
    }

    public void IncGet() => GetCurrent().IncGet();
    public void IncHit() => GetCurrent().IncHit();
    public void IncMiss() => GetCurrent().IncMiss();
    public void IncSet() => GetCurrent().IncSet();
    public void IncRemove() => GetCurrent().IncRemove();

    private CacheStats GetCurrent()
        => string.Equals(_originAccessor.CurrentOrigin, CacheOperationOrigin.DistributedCacheBridge, StringComparison.Ordinal)
            ? _interop
            : _native;
}
