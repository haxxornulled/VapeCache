using Autofac;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Extensions.Aspire.Autofac;

namespace VapeCache.Tests.Aspire;

public sealed class AspireAutofacModuleTests
{
    [Fact]
    public void AutofacModule_BindsConfiguration_AndRegistersCoreServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:redis"] = "redis://cache.internal:6379/2",
                ["RedisMultiplexer:Connections"] = "3",
                ["RedisMultiplexer:MaxInFlightPerConnection"] = "2048"
            })
            .Build();

        var builder = new ContainerBuilder();
        builder.RegisterInstance(NullLoggerFactory.Instance).As<ILoggerFactory>().SingleInstance();
        builder.RegisterGeneric(typeof(NullLogger<>)).As(typeof(ILogger<>)).SingleInstance();
        builder.RegisterModule(new VapeCacheAspireAutofacModule(
            configuration,
            transportProfile: RedisTransportProfile.LowLatency,
            connectionName: "redis"));

        using var container = builder.Build();

        var connectionOptions = container.Resolve<IOptionsMonitor<RedisConnectionOptions>>().CurrentValue;
        var multiplexerOptions = container.Resolve<IOptionsMonitor<RedisMultiplexerOptions>>().CurrentValue;

        Assert.Equal("redis://cache.internal:6379/2", connectionOptions.ConnectionString);
        Assert.Equal(RedisTransportProfile.LowLatency, connectionOptions.TransportProfile);
        Assert.Equal(RedisTransportProfile.LowLatency, multiplexerOptions.TransportProfile);
        Assert.Equal(3, multiplexerOptions.Connections);
        Assert.Equal(2048, multiplexerOptions.MaxInFlightPerConnection);

        Assert.NotNull(container.Resolve<IRedisConnectionPool>());
        Assert.NotNull(container.Resolve<IRedisCommandExecutor>());
        Assert.NotNull(container.Resolve<ICacheService>());
    }

    [Fact]
    public void AutofacModule_OptionsMonitor_ReloadsFromConfigurationChanges()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:redis"] = "redis://cache.internal:6379/2",
                ["RedisMultiplexer:Connections"] = "8",
                ["RedisMultiplexer:AutoAdjustBulkLanes"] = "true",
                ["RedisMultiplexer:BulkLaneTargetRatio"] = "0.25"
            })
            .Build();

        var builder = new ContainerBuilder();
        builder.RegisterInstance(NullLoggerFactory.Instance).As<ILoggerFactory>().SingleInstance();
        builder.RegisterGeneric(typeof(NullLogger<>)).As(typeof(ILogger<>)).SingleInstance();
        builder.RegisterModule(new VapeCacheAspireAutofacModule(
            configuration,
            transportProfile: RedisTransportProfile.FullTilt,
            connectionName: "redis"));

        using var container = builder.Build();
        var multiplexerMonitor = container.Resolve<IOptionsMonitor<RedisMultiplexerOptions>>();

        Assert.True(multiplexerMonitor.CurrentValue.AutoAdjustBulkLanes);
        Assert.Equal(0.25, multiplexerMonitor.CurrentValue.BulkLaneTargetRatio);

        using var changed = new ManualResetEventSlim(false);
        double observedRatio = 0;
        using var subscription = multiplexerMonitor.OnChange((updated, _) =>
        {
            observedRatio = updated.BulkLaneTargetRatio;
            changed.Set();
        });

        configuration["RedisMultiplexer:BulkLaneTargetRatio"] = "0.40";
        ((IConfigurationRoot)configuration).Reload();

        Assert.True(changed.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(0.40, observedRatio, 2);
        Assert.Equal(0.40, multiplexerMonitor.CurrentValue.BulkLaneTargetRatio, 2);
    }
}
