using System.Buffers;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

/// <summary>
/// Benchmarks circuit breaker overhead and failover performance.
/// Compares pure in-memory vs hybrid (circuit open) vs Redis (circuit closed).
/// </summary>
[MemoryDiagnoser]
[Config(typeof(EnterpriseBenchmarkConfig))]
public class CircuitBreakerPerformanceBenchmarks
{
    private ICacheService _inMemory = null!;
    private ICacheService _hybridCircuitOpen = null!;

    private byte[] _payload = null!;
    private TestData _testData = null!;
    private const string TestKey = "circuit:test:key";

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();

        // Add logging (required)
        services.AddLogging();

        // Configure Redis connection options (circuit will open immediately - no real Redis)
        services.Configure<RedisConnectionOptions>(options =>
        {
            // Use reflection to set init-only properties for benchmarking
            typeof(RedisConnectionOptions).GetProperty(nameof(RedisConnectionOptions.Host))!
                .SetValue(options, "invalid-host-benchmark");
            typeof(RedisConnectionOptions).GetProperty(nameof(RedisConnectionOptions.Port))!
                .SetValue(options, 9999);
            typeof(RedisConnectionOptions).GetProperty(nameof(RedisConnectionOptions.ConnectTimeout))!
                .SetValue(options, TimeSpan.FromMilliseconds(100));
        });

        // Register VapeCache
        services.AddVapecacheRedisConnections();
        services.AddVapecacheCaching();

        var provider = services.BuildServiceProvider();

        // Get hybrid cache (will be in-memory mode since Redis is unavailable)
        _hybridCircuitOpen = provider.GetRequiredService<ICacheService>();

        // Create separate pure in-memory cache for baseline comparison
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var currentService = new CurrentCacheService();
        var statsRegistry = new CacheStatsRegistry();
        var spillOptions = new BenchmarkOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions { EnableSpillToDisk = false });
        var spillStore = new NoopSpillStore();
        _inMemory = new InMemoryCacheService(memoryCache, currentService, statsRegistry, spillOptions, spillStore);

        // Prepare test data
        _payload = Encoding.UTF8.GetBytes("Benchmark payload data");
        _testData = new TestData(
            Guid.NewGuid(),
            "Test Name",
            42,
            DateTime.UtcNow,
            new[] { "tag1", "tag2", "tag3" });

        // Warm up
        _inMemory.SetAsync("warmup", _payload, default, CancellationToken.None).AsTask().Wait();
        _hybridCircuitOpen.SetAsync("warmup", _payload, default, CancellationToken.None).AsTask().Wait();
    }

    // ===========================
    // Circuit Breaker Overhead
    // ===========================

    [Benchmark(Baseline = true, Description = "GET - Pure InMemory (no circuit breaker)")]
    public async Task<byte[]?> Get_InMemory_NoCB()
    {
        return await _inMemory.GetAsync(TestKey, CancellationToken.None);
    }

    [Benchmark(Description = "GET - Hybrid (circuit breaker OPEN)")]
    public async Task<byte[]?> Get_Hybrid_CircuitOpen()
    {
        return await _hybridCircuitOpen.GetAsync(TestKey, CancellationToken.None);
    }

    [Benchmark(Description = "SET - Pure InMemory (no circuit breaker)")]
    public async Task Set_InMemory_NoCB()
    {
        await _inMemory.SetAsync(TestKey, _payload, default, CancellationToken.None);
    }

    [Benchmark(Description = "SET - Hybrid (circuit breaker OPEN)")]
    public async Task Set_Hybrid_CircuitOpen()
    {
        await _hybridCircuitOpen.SetAsync(TestKey, _payload, default, CancellationToken.None);
    }

    // ===========================
    // Typed Operations with Circuit Breaker
    // ===========================

    [Benchmark(Description = "GET<T> JSON - Pure InMemory")]
    public async Task<TestData?> GetTyped_InMemory()
    {
        return await _inMemory.GetAsync(
            TestKey,
            span => JsonSerializer.Deserialize<TestData>(span),
            CancellationToken.None);
    }

    [Benchmark(Description = "GET<T> JSON - Hybrid (circuit OPEN)")]
    public async Task<TestData?> GetTyped_Hybrid()
    {
        return await _hybridCircuitOpen.GetAsync(
            TestKey,
            span => JsonSerializer.Deserialize<TestData>(span),
            CancellationToken.None);
    }

    [Benchmark(Description = "SET<T> JSON - Pure InMemory")]
    public async Task SetTyped_InMemory()
    {
        await _inMemory.SetAsync(
            TestKey,
            _testData,
            SerializeJson,
            new CacheEntryOptions(TimeSpan.FromMinutes(10)),
            CancellationToken.None);
    }

    [Benchmark(Description = "SET<T> JSON - Hybrid (circuit OPEN)")]
    public async Task SetTyped_Hybrid()
    {
        await _hybridCircuitOpen.SetAsync(
            TestKey,
            _testData,
            SerializeJson,
            new CacheEntryOptions(TimeSpan.FromMinutes(10)),
            CancellationToken.None);
    }

    // ===========================
    // Cache-Aside with Circuit Breaker
    // ===========================

    [Benchmark(Description = "GetOrSet (Hit) - Pure InMemory")]
    public async Task<TestData> GetOrSet_Hit_InMemory()
    {
        return await _inMemory.GetOrSetAsync(
            TestKey,
            Factory,
            SerializeJson,
            span => JsonSerializer.Deserialize<TestData>(span)!,
            new CacheEntryOptions(TimeSpan.FromMinutes(10)),
            CancellationToken.None);
    }

    [Benchmark(Description = "GetOrSet (Hit) - Hybrid (circuit OPEN)")]
    public async Task<TestData> GetOrSet_Hit_Hybrid()
    {
        return await _hybridCircuitOpen.GetOrSetAsync(
            TestKey,
            Factory,
            SerializeJson,
            span => JsonSerializer.Deserialize<TestData>(span)!,
            new CacheEntryOptions(TimeSpan.FromMinutes(10)),
            CancellationToken.None);
    }

    [Benchmark(Description = "GetOrSet (Miss) - Pure InMemory")]
    public async Task<TestData> GetOrSet_Miss_InMemory()
    {
        var key = $"miss:{Guid.NewGuid()}";
        return await _inMemory.GetOrSetAsync(
            key,
            Factory,
            SerializeJson,
            span => JsonSerializer.Deserialize<TestData>(span)!,
            new CacheEntryOptions(TimeSpan.FromMinutes(10)),
            CancellationToken.None);
    }

    [Benchmark(Description = "GetOrSet (Miss) - Hybrid (circuit OPEN)")]
    public async Task<TestData> GetOrSet_Miss_Hybrid()
    {
        var key = $"miss:{Guid.NewGuid()}";
        return await _hybridCircuitOpen.GetOrSetAsync(
            key,
            Factory,
            SerializeJson,
            span => JsonSerializer.Deserialize<TestData>(span)!,
            new CacheEntryOptions(TimeSpan.FromMinutes(10)),
            CancellationToken.None);
    }

    // ===========================
    // High-Frequency Operations (Throughput Test)
    // ===========================

    [Benchmark(Description = "100 SETs - Pure InMemory")]
    public async Task Throughput_100Sets_InMemory()
    {
        for (int i = 0; i < 100; i++)
        {
            await _inMemory.SetAsync($"key:{i}", _payload, default, CancellationToken.None);
        }
    }

    [Benchmark(Description = "100 SETs - Hybrid (circuit OPEN)")]
    public async Task Throughput_100Sets_Hybrid()
    {
        for (int i = 0; i < 100; i++)
        {
            await _hybridCircuitOpen.SetAsync($"key:{i}", _payload, default, CancellationToken.None);
        }
    }

    [Benchmark(Description = "100 GETs - Pure InMemory")]
    public async Task Throughput_100Gets_InMemory()
    {
        for (int i = 0; i < 100; i++)
        {
            await _inMemory.GetAsync($"key:{i}", CancellationToken.None);
        }
    }

    [Benchmark(Description = "100 GETs - Hybrid (circuit OPEN)")]
    public async Task Throughput_100Gets_Hybrid()
    {
        for (int i = 0; i < 100; i++)
        {
            await _hybridCircuitOpen.GetAsync($"key:{i}", CancellationToken.None);
        }
    }

    [Benchmark(Description = "100 Mixed Ops - Pure InMemory")]
    public async Task Throughput_100Mixed_InMemory()
    {
        for (int i = 0; i < 100; i++)
        {
            if (i % 2 == 0)
                await _inMemory.SetAsync($"key:{i}", _payload, default, CancellationToken.None);
            else
                await _inMemory.GetAsync($"key:{i}", CancellationToken.None);
        }
    }

    [Benchmark(Description = "100 Mixed Ops - Hybrid (circuit OPEN)")]
    public async Task Throughput_100Mixed_Hybrid()
    {
        for (int i = 0; i < 100; i++)
        {
            if (i % 2 == 0)
                await _hybridCircuitOpen.SetAsync($"key:{i}", _payload, default, CancellationToken.None);
            else
                await _hybridCircuitOpen.GetAsync($"key:{i}", CancellationToken.None);
        }
    }

    // ===========================
    // Helper Methods
    // ===========================

    private static void SerializeJson(IBufferWriter<byte> writer, TestData data)
    {
        using var jsonWriter = new Utf8JsonWriter(writer);
        JsonSerializer.Serialize(jsonWriter, data);
    }

    private static ValueTask<TestData> Factory(CancellationToken ct)
    {
        return ValueTask.FromResult(new TestData(
            Guid.NewGuid(),
            "Factory Created",
            99,
            DateTime.UtcNow,
            new[] { "generated" }));
    }
}

public record TestData(
    Guid Id,
    string Name,
    int Value,
    DateTime Timestamp,
    string[] Tags);
