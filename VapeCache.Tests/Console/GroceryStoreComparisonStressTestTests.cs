using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using VapeCache.Abstractions.Caching;
using VapeCache.Console.GroceryStore;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Caching.Codecs;
using VapeCache.Infrastructure.Collections;
using VapeCache.Infrastructure.Connections;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.Console;

public sealed class GroceryStoreComparisonStressTestTests
{
    [Fact]
    public async Task RunStressTestAsync_returns_metrics_for_small_workload()
    {
        await using var h = Harness.Create();
        var sut = new GroceryStoreComparisonStressTest(h.Service, NullLogger<GroceryStoreComparisonStressTest>.Instance, "VapeCache");

        var result = await sut.RunStressTestAsync(shopperCount: 40, maxCartSize: 15);

        Assert.Equal("VapeCache", result.ProviderName);
        Assert.Equal(40, result.ShopperCount);
        Assert.Equal(40, result.SuccessCount + result.ErrorCount);
        Assert.True(result.ThroughputShoppersPerSec > 0);
        Assert.True(result.P99LatencyMs >= result.P95LatencyMs);
    }

    [Fact]
    public async Task RunStressTestAsync_prefers_batch_writer_when_available()
    {
        var service = new BatchCapableFakeService();
        var sut = new GroceryStoreComparisonStressTest(service, NullLogger<GroceryStoreComparisonStressTest>.Instance, "VapeCache");

        var result = await sut.RunStressTestAsync(shopperCount: 20, maxCartSize: 15);

        Assert.Equal(20, result.SuccessCount);
        Assert.Equal(20, service.BatchCalls);
        Assert.Equal(0, service.SingleItemCalls);
        Assert.Equal(300, service.BatchedItemCount);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly InMemoryCommandExecutor _executor;
        private readonly MemoryCache _memoryCache;

        public GroceryStoreService Service { get; }

        private Harness(GroceryStoreService service, InMemoryCommandExecutor executor, MemoryCache memoryCache)
        {
            Service = service;
            _executor = executor;
            _memoryCache = memoryCache;
        }

        public static Harness Create()
        {
            var executor = new InMemoryCommandExecutor();
            var codecs = new SystemTextJsonCodecProvider();
            var collections = new CacheCollectionFactory(executor, codecs);

            var current = new CurrentCacheService();
            var stats = new CacheStatsRegistry();
            var memory = new MemoryCache(new MemoryCacheOptions());
            var spillOptions = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions { EnableSpillToDisk = false });
            var cacheService = new InMemoryCacheService(memory, current, stats, spillOptions, new NoopSpillStore());
            var client = new VapeCacheClient(cacheService, codecs);

            var service = new GroceryStoreService(collections, client, NullLogger<GroceryStoreService>.Instance);
            return new Harness(service, executor, memory);
        }

        public async ValueTask DisposeAsync()
        {
            _memoryCache.Dispose();
            await _executor.DisposeAsync();
        }
    }

    private sealed class BatchCapableFakeService : IGroceryStoreService, ICartBatchWriter
    {
        private readonly Dictionary<string, CartItem[]> _carts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _flashSales = new(StringComparer.Ordinal);
        private readonly Dictionary<string, UserSession> _sessions = new(StringComparer.Ordinal);

        public int BatchCalls;
        public int SingleItemCalls;
        public int BatchedItemCount;

        public ValueTask<Product?> GetProductAsync(string productId)
            => ValueTask.FromResult<Product?>(new Product(productId, "name", "cat", 1m, 1, "/"));

        public ValueTask CacheProductAsync(Product product, TimeSpan ttl) => ValueTask.CompletedTask;

        public ValueTask AddToCartAsync(string userId, CartItem item)
        {
            Interlocked.Increment(ref SingleItemCalls);
            return ValueTask.CompletedTask;
        }

        public ValueTask AddToCartBatchAsync(string userId, IReadOnlyList<CartItem> items)
        {
            Interlocked.Increment(ref BatchCalls);
            Interlocked.Add(ref BatchedItemCount, items.Count);
            lock (_carts)
            {
                _carts[userId] = items.ToArray();
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<CartItem[]> GetCartAsync(string userId)
        {
            lock (_carts)
            {
                return ValueTask.FromResult(_carts.TryGetValue(userId, out var items) ? items : Array.Empty<CartItem>());
            }
        }

        public ValueTask<long> GetCartCountAsync(string userId)
        {
            lock (_carts)
            {
                return ValueTask.FromResult(_carts.TryGetValue(userId, out var items) ? (long)items.Length : 0L);
            }
        }

        public ValueTask ClearCartAsync(string userId)
        {
            lock (_carts)
            {
                _carts.Remove(userId);
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask JoinFlashSaleAsync(string saleId, string userId)
        {
            lock (_flashSales)
            {
                _flashSales.Add($"{saleId}:{userId}");
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> IsInFlashSaleAsync(string saleId, string userId)
        {
            lock (_flashSales)
            {
                return ValueTask.FromResult(_flashSales.Contains($"{saleId}:{userId}"));
            }
        }

        public ValueTask<long> GetFlashSaleParticipantCountAsync(string saleId)
        {
            lock (_flashSales)
            {
                return ValueTask.FromResult((long)_flashSales.Count(entry => entry.StartsWith($"{saleId}:", StringComparison.Ordinal)));
            }
        }

        public ValueTask SaveSessionAsync(string sessionId, UserSession session)
        {
            lock (_sessions)
            {
                _sessions[sessionId] = session;
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<UserSession?> GetSessionAsync(string sessionId)
        {
            lock (_sessions)
            {
                return ValueTask.FromResult(_sessions.TryGetValue(sessionId, out var session) ? session : null);
            }
        }
    }
}
