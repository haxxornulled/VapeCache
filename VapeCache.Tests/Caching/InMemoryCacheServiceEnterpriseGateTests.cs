using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.Caching;

public sealed class InMemoryCacheServiceEnterpriseGateTests
{
    [Fact]
    public async Task SpillPersistence_IsDisabled_WhenSpillLicenseMissing()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var spillOptions = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            EnableSpillToDisk = true,
            SpillThresholdBytes = 8,
            InlinePrefixBytes = 0
        });
        var spillStore = new FakeSpillStore();
        var sut = new InMemoryCacheService(
            memory,
            current,
            stats,
            spillOptions,
            spillStore,
            enterpriseFeatureGate: new TestEnterpriseFeatureGate(spillLicensed: false));

        var payload = new byte[64];
        await sut.SetAsync("k", payload, new CacheEntryOptions(TimeSpan.FromMinutes(5)), CancellationToken.None);
        var fetched = await sut.GetAsync("k", CancellationToken.None);

        Assert.Equal(0, spillStore.WriteCount);
        Assert.NotNull(fetched);
        Assert.Equal(payload.Length, fetched!.Length);
    }

    [Fact]
    public async Task SpillPersistence_IsEnabled_WhenSpillLicensePresent()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var spillOptions = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            EnableSpillToDisk = true,
            SpillThresholdBytes = 8,
            InlinePrefixBytes = 0
        });
        var spillStore = new FakeSpillStore();
        var sut = new InMemoryCacheService(
            memory,
            current,
            stats,
            spillOptions,
            spillStore,
            enterpriseFeatureGate: new TestEnterpriseFeatureGate(spillLicensed: true));

        var payload = new byte[64];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)i;

        await sut.SetAsync("k", payload, new CacheEntryOptions(TimeSpan.FromMinutes(5)), CancellationToken.None);
        var fetched = await sut.GetAsync("k", CancellationToken.None);

        Assert.Equal(1, spillStore.WriteCount);
        Assert.NotNull(fetched);
        Assert.Equal(payload, fetched);
    }

    private sealed class TestEnterpriseFeatureGate(bool spillLicensed) : IEnterpriseFeatureGate
    {
        public bool IsAutoscalerLicensed => false;
        public bool IsDurableSpillLicensed => spillLicensed;
        public bool IsReconciliationLicensed => false;
    }

    private sealed class FakeSpillStore : IInMemorySpillStore
    {
        private readonly ConcurrentDictionary<Guid, byte[]> _storage = new();
        private int _writeCount;

        public int WriteCount => Volatile.Read(ref _writeCount);

        public ValueTask WriteAsync(Guid spillRef, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _storage[spillRef] = data.ToArray();
            Interlocked.Increment(ref _writeCount);
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]?> TryReadAsync(Guid spillRef, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_storage.TryGetValue(spillRef, out var payload) ? payload : null);
        }

        public ValueTask DeleteAsync(Guid spillRef, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _storage.TryRemove(spillRef, out _);
            return ValueTask.CompletedTask;
        }
    }
}
