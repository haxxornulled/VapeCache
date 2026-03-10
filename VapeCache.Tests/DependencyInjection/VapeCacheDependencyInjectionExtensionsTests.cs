using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Extensions.DependencyInjection;

namespace VapeCache.Tests.DependencyInjection;

public sealed class VapeCacheDependencyInjectionExtensionsTests
{
    [Fact]
    public async Task AddVapeCache_RegistersRuntimeServices()
    {
        var services = new ServiceCollection();

        var builder = services.AddVapeCache();

        Assert.NotNull(builder);
        Assert.Contains(services, static d => d.ServiceType == typeof(IRedisConnectionFactory));
        Assert.Contains(services, static d => d.ServiceType == typeof(IRedisConnectionPool));
        Assert.Contains(services, static d => d.ServiceType == typeof(ICacheService));
        Assert.Contains(services, static d => d.ServiceType == typeof(IVapeCache));

        await using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider);
    }

    [Fact]
    public async Task AddVapeCache_WithConfiguration_BindsRuntimeOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RedisConnection:Host"] = "redis.internal",
                ["RedisConnection:Port"] = "6380",
                ["RedisConnection:UseTls"] = "true",
                ["RedisMultiplexer:Connections"] = "6",
                ["RedisCircuitBreaker:ConsecutiveFailuresToOpen"] = "4",
                ["HybridFailover:FallbackWarmReadTtl"] = "00:02:00",
                ["CacheStampede:MaxKeys"] = "64000"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddVapeCache(configuration);

        await using var provider = services.BuildServiceProvider();

        var connection = provider.GetRequiredService<IOptions<RedisConnectionOptions>>().Value;
        var multiplexer = provider.GetRequiredService<IOptions<RedisMultiplexerOptions>>().Value;
        var breaker = provider.GetRequiredService<IOptions<RedisCircuitBreakerOptions>>().Value;
        var failover = provider.GetRequiredService<IOptions<HybridFailoverOptions>>().Value;
        var stampede = provider.GetRequiredService<IOptions<CacheStampedeOptions>>().Value;

        Assert.Equal("redis.internal", connection.Host);
        Assert.Equal(6380, connection.Port);
        Assert.True(connection.UseTls);
        Assert.Equal(6, multiplexer.Connections);
        Assert.Equal(4, breaker.ConsecutiveFailuresToOpen);
        Assert.Equal(TimeSpan.FromMinutes(2), failover.FallbackWarmReadTtl);
        Assert.Equal(64000, stampede.MaxKeys);
    }

    [Fact]
    public async Task AddVapeCache_WithConfiguration_CanSkipSectionBindings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RedisConnection:Host"] = "redis.internal",
                ["CacheStampede:MaxKeys"] = "75000",
                ["ManualRedis:Host"] = "localhost"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddVapeCache(configuration, options =>
        {
            options.BindRedisConnection = false;
            options.BindCacheStampede = false;
        });
        services.AddOptions<RedisConnectionOptions>()
            .Bind(configuration.GetSection("ManualRedis"));

        await using var provider = services.BuildServiceProvider();

        var connection = provider.GetRequiredService<IOptions<RedisConnectionOptions>>().Value;
        var stampede = provider.GetRequiredService<IOptions<CacheStampedeOptions>>().Value;

        Assert.NotEqual("redis.internal", connection.Host);
        Assert.NotEqual(75000, stampede.MaxKeys);
    }
}
