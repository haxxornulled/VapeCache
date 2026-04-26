using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.Caching;

public sealed class HybridCacheServiceTests
{
    [Fact]
    public async Task GetAsyncTyped_TagVersionZero_UsesHealthyRedisPathWithoutFallbackTagRead()
    {
        var fallback = new RecordingFallbackCacheService();
        var service = CreateService(fallback);
        var options = new CacheEntryOptions(Ttl: TimeSpan.FromMinutes(5)).WithTag("product:coffee");
        var payload = Encoding.UTF8.GetBytes("cold-brew");

        await service.SetAsync("product:coffee", payload, options, CancellationToken.None);
        fallback.ResetCounts();

        var result = await service.GetAsync(
            "product:coffee",
            static bytes => Encoding.UTF8.GetString(bytes),
            CancellationToken.None);

        Assert.Equal("cold-brew", result);
        Assert.Equal(0, fallback.GetCalls);
        Assert.DoesNotContain(fallback.RequestedKeys, key => key.StartsWith("vapecache:tag:v1:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetAsyncTyped_MultipleTags_RoundTripsCurrentBinaryEnvelope()
    {
        var fallback = new RecordingFallbackCacheService();
        var service = CreateService(fallback);
        var options = new CacheEntryOptions(Ttl: TimeSpan.FromMinutes(5))
            .WithTags("product:legacy", "category:coffee");

        await service.SetAsync("product:legacy", Encoding.UTF8.GetBytes("legacy-product"), options, CancellationToken.None);

        var result = await service.GetAsync(
            "product:legacy",
            static bytes => Encoding.UTF8.GetString(bytes),
            CancellationToken.None);

        Assert.Equal("legacy-product", result);
    }

    [Fact]
    public async Task GetTagVersionAsync_WithPendingReconciliation_PrefersHigherFallbackVersion()
    {
        var executor = new InMemoryCommandExecutor();
        var fallback = new RecordingFallbackCacheService();
        var reconciliation = new RecordingReconciliationService { PendingOperationsValue = 1 };
        var service = CreateService(
            fallback,
            executor,
            reconciliation,
            new LicensedEnterpriseFeatureGate());
        var tagVersionKey = "vapecache:tag:v1:product:coffee";

        await executor.SetAsync(tagVersionKey, SerializeInt64(1), ttl: null, ct: default);
        await fallback.SetAsync(tagVersionKey, SerializeInt64(2), default, CancellationToken.None);

        var version = await service.GetTagVersionAsync("product:coffee", CancellationToken.None);

        Assert.Equal(2, version);
    }

    [Fact]
    public void ForceOpen_increments_breaker_opened_once_per_manual_open_transition()
    {
        var fallback = new RecordingFallbackCacheService();
        var stats = new CacheStatsRegistry();
        var service = CreateService(fallback, statsRegistry: stats);

        service.ForceOpen("manual-drill");
        service.ForceOpen("manual-drill-repeat");

        var snapshot = stats.GetOrCreate(CacheStatsNames.Hybrid).Snapshot;
        Assert.True(service.IsForcedOpen);
        Assert.Equal("manual-drill-repeat", service.Reason);
        Assert.Equal(1, snapshot.RedisBreakerOpened);
    }

    [Fact]
    public void ForceOpen_after_clear_records_another_breaker_open()
    {
        var fallback = new RecordingFallbackCacheService();
        var stats = new CacheStatsRegistry();
        var service = CreateService(fallback, statsRegistry: stats);

        service.ForceOpen("cycle-1");
        service.ClearForcedOpen();
        service.ForceOpen("cycle-2");

        var snapshot = stats.GetOrCreate(CacheStatsNames.Hybrid).Snapshot;
        Assert.True(service.IsForcedOpen);
        Assert.Equal("cycle-2", service.Reason);
        Assert.Equal(2, snapshot.RedisBreakerOpened);
    }

    private static HybridCacheService CreateService(
        RecordingFallbackCacheService fallback,
        InMemoryCommandExecutor? executor = null,
        IRedisReconciliationService? reconciliation = null,
        IEnterpriseFeatureGate? enterpriseFeatureGate = null,
        CacheStatsRegistry? statsRegistry = null)
    {
        executor ??= new InMemoryCommandExecutor();
        var current = new CurrentCacheService();
        var stats = statsRegistry ?? new CacheStatsRegistry();
        var redis = new RedisCacheService(executor, current, stats);
        return new HybridCacheService(
            redis,
            fallback,
            current,
            TimeProvider.System,
            new TestOptionsMonitor<RedisCircuitBreakerOptions>(new RedisCircuitBreakerOptions { Enabled = true }),
            stats,
            NullLogger<HybridCacheService>.Instance,
            new TestOptionsMonitor<HybridFailoverOptions>(new HybridFailoverOptions
            {
                MirrorWritesToFallbackWhenRedisHealthy = false,
                WarmFallbackOnRedisReadHit = false,
                RemoveStaleFallbackOnRedisMiss = false
            }),
            reconciliation,
            enterpriseFeatureGate);
    }

    private static byte[] SerializeInt64(long value)
    {
        var buffer = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        return buffer;
    }

    private sealed class RecordingFallbackCacheService : ICacheFallbackService
    {
        private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);
        private readonly List<string> _requestedKeys = [];

        public string Name => "memory";
        public int GetCalls { get; private set; }
        public IReadOnlyList<string> RequestedKeys => _requestedKeys;

        public void ResetCounts()
        {
            GetCalls = 0;
            _requestedKeys.Clear();
        }

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            GetCalls++;
            _requestedKeys.Add(key);
            return ValueTask.FromResult(_store.TryGetValue(key, out var payload) ? payload.ToArray() : null);
        }

        public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _store[key] = value.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> RemoveAsync(string key, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_store.Remove(key));
        }

        public async ValueTask<T?> GetAsync<T>(string key, SpanDeserializer<T> deserialize, CancellationToken ct)
        {
            var payload = await GetAsync(key, ct);
            return payload is null ? default : deserialize(payload);
        }

        public ValueTask SetAsync<T>(string key, T value, Action<IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct)
        {
            var writer = new ArrayBufferWriter<byte>();
            serialize(writer, value);
            return SetAsync(key, writer.WrittenMemory, options, ct);
        }

        public async ValueTask<T> GetOrSetAsync<T>(
            string key,
            Func<CancellationToken, ValueTask<T>> factory,
            Action<IBufferWriter<byte>, T> serialize,
            SpanDeserializer<T> deserialize,
            CacheEntryOptions options,
            CancellationToken ct)
        {
            var existing = await GetAsync(key, deserialize, ct);
            if (existing is not null)
                return existing;

            var created = await factory(ct);
            await SetAsync(key, created, serialize, options, ct);
            return created;
        }
    }

    private sealed class RecordingReconciliationService : IRedisReconciliationService
    {
        public int PendingOperationsValue { get; set; }
        public int PendingOperations => PendingOperationsValue;
        public void TrackWrite(string key, ReadOnlyMemory<byte> value, TimeSpan? expiry) { }
        public void TrackDelete(string key) { }
        public ValueTask ReconcileAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public void Clear() { }
        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private sealed class LicensedEnterpriseFeatureGate : IEnterpriseFeatureGate
    {
        public bool IsAutoscalerLicensed => false;
        public bool IsDurableSpillLicensed => false;
        public bool IsReconciliationLicensed => true;
    }
}
