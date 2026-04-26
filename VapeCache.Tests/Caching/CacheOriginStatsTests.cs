using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Tests.Caching;

public sealed class CacheOriginStatsTests
{
    [Fact]
    public void Snapshot_defaults_to_zero_counts()
    {
        var accessor = new CacheOperationOriginAccessor();
        var sut = new CacheOriginStats(accessor);

        var snapshot = sut.Snapshot;

        Assert.Equal(0, snapshot.NativeReads);
        Assert.Equal(0, snapshot.NativeWrites);
        Assert.Equal(0, snapshot.NativeHits);
        Assert.Equal(0, snapshot.NativeMisses);
        Assert.Equal(0, snapshot.InteropReads);
        Assert.Equal(0, snapshot.InteropWrites);
        Assert.Equal(0, snapshot.InteropHits);
        Assert.Equal(0, snapshot.InteropMisses);
    }

    [Fact]
    public void Snapshot_tracks_native_counts_by_default()
    {
        var accessor = new CacheOperationOriginAccessor();
        var sut = new CacheOriginStats(accessor);

        sut.IncGet();
        sut.IncHit();
        sut.IncSet();
        sut.IncRemove();

        var snapshot = sut.Snapshot;

        Assert.Equal(1, snapshot.NativeReads);
        Assert.Equal(2, snapshot.NativeWrites);
        Assert.Equal(1, snapshot.NativeHits);
        Assert.Equal(0, snapshot.NativeMisses);
        Assert.Equal(0, snapshot.InteropReads);
        Assert.Equal(0, snapshot.InteropWrites);
    }

    [Fact]
    public void Snapshot_tracks_interop_counts_inside_bridge_scope()
    {
        var accessor = new CacheOperationOriginAccessor();
        var sut = new CacheOriginStats(accessor);

        using (accessor.BeginScope(CacheOperationOrigin.DistributedCacheBridge))
        {
            sut.IncGet();
            sut.IncMiss();
            sut.IncSet();
        }

        var snapshot = sut.Snapshot;

        Assert.Equal(0, snapshot.NativeReads);
        Assert.Equal(0, snapshot.NativeWrites);
        Assert.Equal(1, snapshot.InteropReads);
        Assert.Equal(1, snapshot.InteropWrites);
        Assert.Equal(0, snapshot.InteropHits);
        Assert.Equal(1, snapshot.InteropMisses);
    }

    [Fact]
    public void OriginAccessor_restores_previous_origin_when_scope_disposes()
    {
        var accessor = new CacheOperationOriginAccessor();

        Assert.Equal(CacheOperationOrigin.Native, accessor.CurrentOrigin);

        using (accessor.BeginScope(CacheOperationOrigin.DistributedCacheBridge))
        {
            Assert.Equal(CacheOperationOrigin.DistributedCacheBridge, accessor.CurrentOrigin);
        }

        Assert.Equal(CacheOperationOrigin.Native, accessor.CurrentOrigin);
    }
}
