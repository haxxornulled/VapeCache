using Microsoft.Extensions.Logging.Abstractions;
using VapeCache.Abstractions.Modules;
using VapeCache.Infrastructure.Connections;
using VapeCache.Infrastructure.Modules;

namespace VapeCache.Tests.Modules;

public sealed class RedisModuleServiceTests
{
    [Fact]
    public async Task Bloom_Fallback_TracksItems_WhenModuleUnavailable()
    {
        var executor = new InMemoryCommandExecutor();
        var modules = new FakeModuleDetector();
        var service = new RedisBloomService(executor, modules, NullLogger<RedisBloomService>.Instance);

        var added = await service.AddAsync("bf:key", new byte[] { 1, 2, 3 });
        var duplicate = await service.AddAsync("bf:key", new byte[] { 1, 2, 3 });
        var exists = await service.ExistsAsync("bf:key", new byte[] { 1, 2, 3 });

        Assert.True(added);
        Assert.False(duplicate);
        Assert.True(exists);
    }

    [Fact]
    public async Task TimeSeries_Fallback_StoresAndRanges()
    {
        var executor = new InMemoryCommandExecutor();
        var modules = new FakeModuleDetector();
        var service = new RedisTimeSeriesService(executor, modules, NullLogger<RedisTimeSeriesService>.Instance);

        await service.CreateSeriesAsync("ts:key");
        await service.AddAsync("ts:key", 100, 1.5);
        await service.AddAsync("ts:key", 200, 2.5);

        var range = await service.RangeAsync("ts:key", 0, 150);
        Assert.Single(range);
        Assert.Equal(100, range[0].Timestamp);
        Assert.Equal(1.5, range[0].Value);
    }

    [Fact]
    public async Task Search_ReturnsEmpty_WhenModuleUnavailable()
    {
        var executor = new InMemoryCommandExecutor();
        var modules = new FakeModuleDetector();
        var service = new RedisSearchService(executor, modules, NullLogger<RedisSearchService>.Instance);

        var created = await service.CreateIndexAsync("idx", "doc:", new[] { "title", "body" });
        var results = await service.SearchAsync("idx", "*");

        Assert.False(created);
        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_UsesExecutor_WhenModuleAvailable()
    {
        var executor = new InMemoryCommandExecutor();
        var modules = new FakeModuleDetector();
        modules.InstalledModules.Add("search");
        var service = new RedisSearchService(executor, modules, NullLogger<RedisSearchService>.Instance);

        var created = await service.CreateIndexAsync("idx", "doc:", new[] { "title" });
        var results = await service.SearchAsync("idx", "*");

        Assert.True(created);
        Assert.Empty(results);
    }

    private sealed class FakeModuleDetector : IRedisModuleDetector
    {
        public HashSet<string> InstalledModules { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ValueTask<bool> IsModuleInstalledAsync(string moduleName, CancellationToken ct = default)
            => ValueTask.FromResult(InstalledModules.Contains(moduleName));

        public ValueTask<string[]> GetInstalledModulesAsync(CancellationToken ct = default)
            => ValueTask.FromResult(InstalledModules.ToArray());

        public ValueTask<bool> HasRedisJsonAsync(CancellationToken ct = default)
            => ValueTask.FromResult(InstalledModules.Contains("ReJSON"));
    }
}
