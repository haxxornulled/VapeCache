using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Extensions.KeyDB;

namespace VapeCache.Tests.DependencyInjection;

public sealed class VapeCacheKeyDbServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddVapeCacheKeyDb_RegistersRuntimeServices()
    {
        var services = new ServiceCollection();

        var builder = services.AddVapeCacheKeyDb();

        Assert.NotNull(builder);
        Assert.Contains(services, static d => d.ServiceType == typeof(IRedisConnectionFactory));
        Assert.Contains(services, static d => d.ServiceType == typeof(IRedisConnectionPool));

        await using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider);
    }

    [Fact]
    public async Task AddVapeCacheKeyDb_WithConfiguration_BindsKeyDbConnectionSectionByDefault()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KeyDbConnection:Host"] = "keydb.internal",
                ["KeyDbConnection:Port"] = "6391",
                ["KeyDbConnection:Database"] = "2",
                ["RedisConnection:Host"] = "redis.internal"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddVapeCacheKeyDb(configuration);

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RedisConnectionOptions>>().Value;

        Assert.Equal("keydb.internal", options.Host);
        Assert.Equal(6391, options.Port);
        Assert.Equal(2, options.Database);
    }

    [Fact]
    public async Task AddVapeCacheKeyDb_WithBindingOverride_CanUseCustomSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KeyDbCluster:Host"] = "cluster.keydb.internal",
                ["KeyDbCluster:Port"] = "7391"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddVapeCacheKeyDb(configuration, options =>
        {
            options.RedisConnectionSectionName = "KeyDbCluster";
        });

        await using var provider = services.BuildServiceProvider();
        var connection = provider.GetRequiredService<IOptions<RedisConnectionOptions>>().Value;

        Assert.Equal("cluster.keydb.internal", connection.Host);
        Assert.Equal(7391, connection.Port);
    }
}
