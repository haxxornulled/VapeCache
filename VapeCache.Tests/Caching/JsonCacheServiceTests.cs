using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Modules;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;
using VapeCache.Persistence;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.Caching;

public sealed class JsonCacheServiceTests
{
    [Fact]
    public async Task JsonCache_FallsBack_ToCache_WhenModuleUnavailable()
    {
        var executor = new InMemoryCommandExecutor();
        var cache = CreateMemoryCacheService();
        var modules = new FakeModuleDetector { RedisJsonAvailable = false };
        var service = new JsonCacheService(executor, cache, modules, NullLogger<JsonCacheService>.Instance);

        var payload = new Widget { Id = 1, Name = "alpha" };
        await service.SetAsync("widgets:1", payload, path: null);

        var result = await service.GetAsync<Widget>("widgets:1");
        Assert.NotNull(result);
        Assert.Equal(1, result!.Id);
        Assert.Equal("alpha", result.Name);

        var deleted = await service.DeleteAsync("widgets:1");
        Assert.Equal(1, deleted);
        Assert.Null(await service.GetAsync<Widget>("widgets:1"));
    }

    [Fact]
    public async Task JsonCache_UsesRedisJson_WhenModuleAvailable()
    {
        var executor = new InMemoryCommandExecutor();
        var cache = CreateMemoryCacheService();
        var modules = new FakeModuleDetector { RedisJsonAvailable = true };
        var service = new JsonCacheService(executor, cache, modules, NullLogger<JsonCacheService>.Instance);

        var payload = new Widget { Id = 7, Name = "beta" };
        await service.SetAsync("widgets:7", payload, path: ".");

        var result = await service.GetAsync<Widget>("widgets:7");
        Assert.NotNull(result);
        Assert.Equal(7, result!.Id);
        Assert.Equal("beta", result.Name);

        var deleted = await service.DeleteAsync("widgets:7");
        Assert.Equal(1, deleted);
        Assert.Null(await service.GetAsync<Widget>("widgets:7"));
    }

    [Fact]
    public async Task JsonCache_Availability_Rechecks_UntilModuleIsDetected()
    {
        var executor = new InMemoryCommandExecutor();
        var cache = CreateMemoryCacheService();
        var modules = new FakeModuleDetector { RedisJsonAvailable = false };
        var service = new JsonCacheService(executor, cache, modules, NullLogger<JsonCacheService>.Instance);

        Assert.False(await service.IsAvailableAsync());

        modules.RedisJsonAvailable = true;

        Assert.True(await service.IsAvailableAsync());
    }

    [Fact]
    public async Task JsonCache_GetLease_FallsBack_ToCache_WhenModuleUnavailable()
    {
        var executor = new InMemoryCommandExecutor();
        var cache = CreateMemoryCacheService();
        var modules = new FakeModuleDetector { RedisJsonAvailable = false };
        var service = new JsonCacheService(executor, cache, modules, NullLogger<JsonCacheService>.Instance);

        var payload = new Widget { Id = 3, Name = "lease-fallback" };
        await service.SetAsync("widgets:3", payload, path: null);

        var lease = await service.GetLeaseAsync("widgets:3");
        Assert.False(lease.IsNull);
        var result = JsonSerializer.Deserialize<Widget>(lease.Span, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        lease.Dispose();

        Assert.NotNull(result);
        Assert.Equal(3, result!.Id);
        Assert.Equal("lease-fallback", result.Name);
    }

    [Fact]
    public async Task JsonCache_SetLease_UsesRedisJson_WhenModuleAvailable()
    {
        var executor = new InMemoryCommandExecutor();
        var cache = CreateMemoryCacheService();
        var modules = new FakeModuleDetector { RedisJsonAvailable = true };
        var service = new JsonCacheService(executor, cache, modules, NullLogger<JsonCacheService>.Instance);

        var payload = new Widget { Id = 9, Name = "lease-set" };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await executor.SetAsync("tmp:lease", bytes, null, default);
        var lease = await executor.GetLeaseAsync("tmp:lease", default);

        await service.SetLeaseAsync("widgets:9", lease, path: ".");
        lease.Dispose();

        var result = await service.GetAsync<Widget>("widgets:9");
        Assert.NotNull(result);
        Assert.Equal(9, result!.Id);
        Assert.Equal("lease-set", result.Name);
    }

    private static ICacheService CreateMemoryCacheService()
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var current = new CurrentCacheService();
        var stats = new CacheStatsRegistry();
        var spillOptions = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions { EnableSpillToDisk = false });
        var spillStore = new FileSpillStore(spillOptions, new NoopSpillEncryptionProvider());
        return new InMemoryCacheService(memoryCache, current, stats, spillOptions, spillStore);
    }

    private sealed class FakeModuleDetector : IRedisModuleDetector
    {
        public bool RedisJsonAvailable { get; set; }

        public ValueTask<bool> IsModuleInstalledAsync(string moduleName, CancellationToken ct = default)
            => ValueTask.FromResult(false);

        public ValueTask<string[]> GetInstalledModulesAsync(CancellationToken ct = default)
            => ValueTask.FromResult(Array.Empty<string>());

        public ValueTask<bool> HasRedisJsonAsync(CancellationToken ct = default)
            => ValueTask.FromResult(RedisJsonAvailable);
    }

    private sealed record Widget
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
    }
}
