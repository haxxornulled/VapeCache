using System.Buffers;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Collections;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

/// <summary>
/// Benchmarks for VapeCache public API methods.
/// Compares in-memory vs hybrid (with circuit open) performance.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(EnterpriseBenchmarkConfig))]
public class CacheServiceApiBenchmarks
{
    private ICacheService _inMemoryCache = null!;
    private ICacheService _hybridCache = null!;
    private ICacheCollectionFactory _collections = null!;

    private byte[] _smallPayload = null!;
    private byte[] _mediumPayload = null!;
    private byte[] _largePayload = null!;

    private TestUser _testUser = null!;
    private string _testKey = null!;

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
        _hybridCache = provider.GetRequiredService<ICacheService>();
        _collections = provider.GetRequiredService<ICacheCollectionFactory>();

        // Create separate pure in-memory cache for baseline comparison
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var currentService = new CurrentCacheService();
        var statsRegistry = new CacheStatsRegistry();
        var spillOptions = Options.Create(new InMemorySpillOptions { EnableSpillToDisk = false });
        var spillStore = new FileSpillStore(spillOptions, new NoopSpillEncryptionProvider());
        _inMemoryCache = new InMemoryCacheService(memoryCache, currentService, statsRegistry, spillOptions, spillStore);

        // Prepare test data
        _smallPayload = Encoding.UTF8.GetBytes("Hello, World!");
        _mediumPayload = new byte[1024]; // 1 KB
        _largePayload = new byte[10240]; // 10 KB

        Random.Shared.NextBytes(_mediumPayload);
        Random.Shared.NextBytes(_largePayload);

        _testUser = new TestUser("John Doe", "john@example.com", 30);
        _testKey = "benchmark:user:123";

        // Warm up caches
        _inMemoryCache.SetAsync("warmup", _smallPayload, default, CancellationToken.None).AsTask().Wait();
        _hybridCache.SetAsync("warmup", _smallPayload, default, CancellationToken.None).AsTask().Wait();
    }

    // ===========================
    // Raw Byte[] Operations
    // ===========================

    [Benchmark(Description = "Set (Small 13B) - InMemory")]
    public async Task Set_Small_InMemory()
    {
        await _inMemoryCache.SetAsync("key:small", _smallPayload, default, CancellationToken.None);
    }

    [Benchmark(Description = "Set (Small 13B) - Hybrid")]
    public async Task Set_Small_Hybrid()
    {
        await _hybridCache.SetAsync("key:small", _smallPayload, default, CancellationToken.None);
    }

    [Benchmark(Description = "Set (Medium 1KB) - InMemory")]
    public async Task Set_Medium_InMemory()
    {
        await _inMemoryCache.SetAsync("key:medium", _mediumPayload, default, CancellationToken.None);
    }

    [Benchmark(Description = "Set (Medium 1KB) - Hybrid")]
    public async Task Set_Medium_Hybrid()
    {
        await _hybridCache.SetAsync("key:medium", _mediumPayload, default, CancellationToken.None);
    }

    [Benchmark(Description = "Set (Large 10KB) - InMemory")]
    public async Task Set_Large_InMemory()
    {
        await _inMemoryCache.SetAsync("key:large", _largePayload, default, CancellationToken.None);
    }

    [Benchmark(Description = "Set (Large 10KB) - Hybrid")]
    public async Task Set_Large_Hybrid()
    {
        await _hybridCache.SetAsync("key:large", _largePayload, default, CancellationToken.None);
    }

    [Benchmark(Description = "Get (Small 13B) - InMemory")]
    public async Task<byte[]?> Get_Small_InMemory()
    {
        return await _inMemoryCache.GetAsync("key:small", CancellationToken.None);
    }

    [Benchmark(Description = "Get (Small 13B) - Hybrid")]
    public async Task<byte[]?> Get_Small_Hybrid()
    {
        return await _hybridCache.GetAsync("key:small", CancellationToken.None);
    }

    [Benchmark(Description = "Get (Medium 1KB) - InMemory")]
    public async Task<byte[]?> Get_Medium_InMemory()
    {
        return await _inMemoryCache.GetAsync("key:medium", CancellationToken.None);
    }

    [Benchmark(Description = "Get (Medium 1KB) - Hybrid")]
    public async Task<byte[]?> Get_Medium_Hybrid()
    {
        return await _hybridCache.GetAsync("key:medium", CancellationToken.None);
    }

    // ===========================
    // Zero-Allocation Typed Operations
    // ===========================

    [Benchmark(Description = "GetAsync<T> JSON - InMemory")]
    public async Task<TestUser?> GetTyped_Json_InMemory()
    {
        return await _inMemoryCache.GetAsync(
            _testKey,
            span => JsonSerializer.Deserialize<TestUser>(span),
            CancellationToken.None);
    }

    [Benchmark(Description = "GetAsync<T> JSON - Hybrid")]
    public async Task<TestUser?> GetTyped_Json_Hybrid()
    {
        return await _hybridCache.GetAsync(
            _testKey,
            span => JsonSerializer.Deserialize<TestUser>(span),
            CancellationToken.None);
    }

    [Benchmark(Description = "SetAsync<T> JSON - InMemory")]
    public async Task SetTyped_Json_InMemory()
    {
        await _inMemoryCache.SetAsync(
            _testKey,
            _testUser,
            (writer, user) =>
            {
                using var jsonWriter = new Utf8JsonWriter(writer);
                JsonSerializer.Serialize(jsonWriter, user);
            },
            new CacheEntryOptions(TimeSpan.FromMinutes(10)),
            CancellationToken.None);
    }

    [Benchmark(Description = "SetAsync<T> JSON - Hybrid")]
    public async Task SetTyped_Json_Hybrid()
    {
        await _hybridCache.SetAsync(
            _testKey,
            _testUser,
            (writer, user) =>
            {
                using var jsonWriter = new Utf8JsonWriter(writer);
                JsonSerializer.Serialize(jsonWriter, user);
            },
            new CacheEntryOptions(TimeSpan.FromMinutes(10)),
            CancellationToken.None);
    }

    // ===========================
    // Cache-Aside Pattern (GetOrSet)
    // ===========================

    [Benchmark(Description = "GetOrSetAsync (Cache Hit) - InMemory")]
    public async Task<TestUser> GetOrSet_Hit_InMemory()
    {
        return await _inMemoryCache.GetOrSetAsync(
            _testKey,
            async ct =>
            {
                await Task.Delay(1, ct); // Simulate DB query
                return _testUser;
            },
            (writer, user) =>
            {
                using var jsonWriter = new Utf8JsonWriter(writer);
                JsonSerializer.Serialize(jsonWriter, user);
            },
            span => JsonSerializer.Deserialize<TestUser>(span)!,
            new CacheEntryOptions(TimeSpan.FromMinutes(10)),
            CancellationToken.None);
    }

    [Benchmark(Description = "GetOrSetAsync (Cache Hit) - Hybrid")]
    public async Task<TestUser> GetOrSet_Hit_Hybrid()
    {
        return await _hybridCache.GetOrSetAsync(
            _testKey,
            async ct =>
            {
                await Task.Delay(1, ct); // Simulate DB query
                return _testUser;
            },
            (writer, user) =>
            {
                using var jsonWriter = new Utf8JsonWriter(writer);
                JsonSerializer.Serialize(jsonWriter, user);
            },
            span => JsonSerializer.Deserialize<TestUser>(span)!,
            new CacheEntryOptions(TimeSpan.FromMinutes(10)),
            CancellationToken.None);
    }

    [Benchmark(Description = "GetOrSetAsync (Cache Miss) - InMemory")]
    public async Task<TestUser> GetOrSet_Miss_InMemory()
    {
        var key = $"miss:{Guid.NewGuid()}";
        return await _inMemoryCache.GetOrSetAsync(
            key,
            async ct =>
            {
                await Task.Delay(1, ct); // Simulate DB query
                return _testUser;
            },
            (writer, user) =>
            {
                using var jsonWriter = new Utf8JsonWriter(writer);
                JsonSerializer.Serialize(jsonWriter, user);
            },
            span => JsonSerializer.Deserialize<TestUser>(span)!,
            new CacheEntryOptions(TimeSpan.FromMinutes(10)),
            CancellationToken.None);
    }

    [Benchmark(Description = "GetOrSetAsync (Cache Miss) - Hybrid")]
    public async Task<TestUser> GetOrSet_Miss_Hybrid()
    {
        var key = $"miss:{Guid.NewGuid()}";
        return await _hybridCache.GetOrSetAsync(
            key,
            async ct =>
            {
                await Task.Delay(1, ct); // Simulate DB query
                return _testUser;
            },
            (writer, user) =>
            {
                using var jsonWriter = new Utf8JsonWriter(writer);
                JsonSerializer.Serialize(jsonWriter, user);
            },
            span => JsonSerializer.Deserialize<TestUser>(span)!,
            new CacheEntryOptions(TimeSpan.FromMinutes(10)),
            CancellationToken.None);
    }

    // ===========================
    // Typed Collections API
    // ===========================

    [Benchmark(Description = "List.PushBackAsync - Hybrid")]
    public async Task List_PushBack()
    {
        var list = _collections.List<TestUser>("benchmark:list");
        await list.PushBackAsync(_testUser);
    }

    [Benchmark(Description = "List.PopFrontAsync - Hybrid")]
    public async Task<TestUser?> List_PopFront()
    {
        var list = _collections.List<TestUser>("benchmark:list");
        return await list.PopFrontAsync();
    }

    [Benchmark(Description = "List.LengthAsync - Hybrid")]
    public async Task<long> List_Length()
    {
        var list = _collections.List<TestUser>("benchmark:list");
        return await list.LengthAsync();
    }

    [Benchmark(Description = "Set.AddAsync - Hybrid")]
    public async Task<long> Set_Add()
    {
        var set = _collections.Set<string>("benchmark:set");
        return await set.AddAsync("item-" + Guid.NewGuid());
    }

    [Benchmark(Description = "Set.ContainsAsync - Hybrid")]
    public async Task<bool> Set_Contains()
    {
        var set = _collections.Set<string>("benchmark:set");
        return await set.ContainsAsync("test-item");
    }

    [Benchmark(Description = "Set.CountAsync - Hybrid")]
    public async Task<long> Set_Count()
    {
        var set = _collections.Set<string>("benchmark:set");
        return await set.CountAsync();
    }

    [Benchmark(Description = "Hash.SetAsync - Hybrid")]
    public async Task<long> Hash_Set()
    {
        var hash = _collections.Hash<string>("benchmark:hash");
        return await hash.SetAsync("field1", "value1");
    }

    [Benchmark(Description = "Hash.GetAsync - Hybrid")]
    public async Task<string?> Hash_Get()
    {
        var hash = _collections.Hash<string>("benchmark:hash");
        return await hash.GetAsync("field1");
    }

    [Benchmark(Description = "Hash.GetManyAsync (3 fields) - Hybrid")]
    public async Task<string?[]> Hash_GetMany()
    {
        var hash = _collections.Hash<string>("benchmark:hash");
        return await hash.GetManyAsync(new[] { "field1", "field2", "field3" });
    }

    // ===========================
    // Remove Operations
    // ===========================

    [Benchmark(Description = "RemoveAsync - InMemory")]
    public async Task<bool> Remove_InMemory()
    {
        return await _inMemoryCache.RemoveAsync("key:remove", CancellationToken.None);
    }

    [Benchmark(Description = "RemoveAsync - Hybrid")]
    public async Task<bool> Remove_Hybrid()
    {
        return await _hybridCache.RemoveAsync("key:remove", CancellationToken.None);
    }
}

public record TestUser(string Name, string Email, int Age);
