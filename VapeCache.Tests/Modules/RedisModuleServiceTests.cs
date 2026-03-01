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
        var fallback = new InMemoryCommandExecutor();
        var modules = new FakeModuleDetector();
        var service = new RedisBloomService(executor, fallback, modules, NullLogger<RedisBloomService>.Instance);

        var added = await service.AddAsync("bf:key", new byte[] { 1, 2, 3 });
        var duplicate = await service.AddAsync("bf:key", new byte[] { 1, 2, 3 });
        var exists = await service.ExistsAsync("bf:key", new byte[] { 1, 2, 3 });

        Assert.True(added);
        Assert.False(duplicate);
        Assert.True(exists);
        Assert.False(await executor.BfExistsAsync("bf:key", new byte[] { 1, 2, 3 }, default));
        Assert.True(await fallback.BfExistsAsync("bf:key", new byte[] { 1, 2, 3 }, default));
    }

    [Fact]
    public async Task Bloom_Availability_Rechecks_UntilModuleIsDetected()
    {
        var executor = new InMemoryCommandExecutor();
        var fallback = new InMemoryCommandExecutor();
        var modules = new FakeModuleDetector();
        var service = new RedisBloomService(executor, fallback, modules, NullLogger<RedisBloomService>.Instance);

        Assert.False(await service.IsAvailableAsync());

        modules.InstalledModules.Add("bf");

        Assert.True(await service.IsAvailableAsync());
    }

    [Fact]
    public async Task TimeSeries_Fallback_StoresAndRanges()
    {
        var executor = new InMemoryCommandExecutor();
        var fallback = new InMemoryCommandExecutor();
        var modules = new FakeModuleDetector();
        var service = new RedisTimeSeriesService(executor, fallback, modules, NullLogger<RedisTimeSeriesService>.Instance);

        await service.CreateSeriesAsync("ts:key");
        await service.AddAsync("ts:key", 100, 1.5);
        await service.AddAsync("ts:key", 200, 2.5);

        var range = await service.RangeAsync("ts:key", 0, 150);
        var fallbackRange = await fallback.TsRangeAsync("ts:key", 0, 500, default);

        Assert.Single(range);
        Assert.Equal(100, range[0].Timestamp);
        Assert.Equal(1.5, range[0].Value);
        Assert.Equal(2, fallbackRange.Length);
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
    public async Task Search_Availability_Rechecks_UntilModuleIsDetected()
    {
        var executor = new InMemoryCommandExecutor();
        var modules = new FakeModuleDetector();
        var service = new RedisSearchService(executor, modules, NullLogger<RedisSearchService>.Instance);

        Assert.False(await service.IsAvailableAsync());

        modules.InstalledModules.Add("search");

        Assert.True(await service.IsAvailableAsync());
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
