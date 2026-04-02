using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VapeCache.Extensions.DependencyInjection;
using VapeCache.Extensions.DistributedCache;

namespace VapeCache.Tests.DependencyInjection;

public sealed class VapeCacheDistributedCacheExtensionsTests
{
    [Fact]
    public void AddVapeCacheDistributedCache_ThrowsWhenServicesIsNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            VapeCacheDistributedCacheServiceCollectionExtensions.AddVapeCacheDistributedCache(
                services: null!,
                static _ => { }));
    }

    [Fact]
    public void AddVapeCacheDistributedCache_WithConfiguration_ThrowsWhenServicesIsNull()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Throws<ArgumentNullException>(() =>
            VapeCacheDistributedCacheServiceCollectionExtensions.AddVapeCacheDistributedCache(
                services: null!,
                configuration,
                sectionName: "VapeCacheDistributedCache"));
    }

    [Fact]
    public void AddVapeCacheDistributedCache_WithConfiguration_ThrowsWhenConfigurationIsNull()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() =>
            services.AddVapeCacheDistributedCache(
                configuration: null!,
                sectionName: "VapeCacheDistributedCache"));
    }

    [Fact]
    public void AddVapeCacheDistributedCache_WithConfiguration_ThrowsWhenSectionIsWhitespace()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        Assert.Throws<ArgumentException>(() =>
            services.AddVapeCacheDistributedCache(configuration, "   "));
    }

    [Fact]
    public void UseDistributedCacheAdapter_ThrowsWhenBuilderIsNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            VapeCacheDistributedCacheBuilderExtensions.UseDistributedCacheAdapter(
                builder: null!,
                static _ => { }));
    }

    [Fact]
    public void AddVapeCache_DoesNotRegisterDistributedCacheByDefault()
    {
        var services = new ServiceCollection();
        services.AddVapeCache();

        Assert.DoesNotContain(
            services,
            static descriptor => descriptor.ServiceType == typeof(IDistributedCache));
    }

    [Fact]
    public async Task AddVapeCacheDistributedCache_RegistersAdapter_AndBindsOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RedisConnection:Host"] = "localhost",
                ["VapeCacheDistributedCache:KeyPrefix"] = "fusion:l2:"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddVapeCache(configuration);
        services.AddVapeCacheDistributedCache(configuration);

        await using var provider = services.BuildServiceProvider();
        var adapter = provider.GetRequiredService<VapeCacheDistributedCache>();
        var distributedCache = provider.GetRequiredService<IDistributedCache>();
        var bufferDistributedCache = provider.GetRequiredService<IBufferDistributedCache>();
        var options = provider.GetRequiredService<IOptions<VapeCacheDistributedCacheOptions>>().Value;

        Assert.Equal("fusion:l2:", options.KeyPrefix);
        Assert.Same(adapter, distributedCache);
        Assert.Same(adapter, bufferDistributedCache);
    }

    [Fact]
    public void UseDistributedCacheAdapter_OnBuilder_RegistersDistributedCache()
    {
        var services = new ServiceCollection();
        services.AddVapeCache()
            .UseDistributedCacheAdapter(static options => options.KeyPrefix = "interop:");

        Assert.Contains(
            services,
            static descriptor => descriptor.ServiceType == typeof(IDistributedCache));
        Assert.Contains(
            services,
            static descriptor => descriptor.ServiceType == typeof(IBufferDistributedCache));
    }

    [Fact]
    public async Task AddVapeCacheInMemory_CanRegisterDistributedCacheAdapter()
    {
        var services = new ServiceCollection();
        services.AddVapeCacheInMemory()
            .UseDistributedCacheAdapter(static options => options.KeyPrefix = "local:");

        await using var provider = services.BuildServiceProvider();
        var distributedCache = provider.GetRequiredService<IDistributedCache>();

        await distributedCache.SetStringAsync("hello", "world");
        var value = await distributedCache.GetStringAsync("hello");

        Assert.Equal("world", value);
    }
}
