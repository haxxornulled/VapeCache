using Autofac;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Extensions.DependencyInjection;
using VapeCache.Extensions.PubSub;
using Moq;

namespace VapeCache.Tests.DependencyInjection;

public sealed class VapeCachePubSubExtensionsTests
{
    [Fact]
    public void AddVapeCache_DoesNotRegisterPubSubByDefault()
    {
        var services = new ServiceCollection();
        services.AddVapeCache();

        Assert.DoesNotContain(
            services,
            static descriptor => descriptor.ServiceType == typeof(IRedisPubSubService));
    }

    [Fact]
    public async Task AddVapeCachePubSub_RegistersService_AndBindsOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RedisPubSub:DeliveryQueueCapacity"] = "96",
                ["RedisPubSub:DropOldestOnBackpressure"] = "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddVapeCache();
        services.AddVapeCachePubSub(configuration);

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RedisPubSubOptions>>().Value;
        var service = provider.GetRequiredService<IRedisPubSubService>();

        Assert.Equal(96, options.DeliveryQueueCapacity);
        Assert.False(options.DropOldestOnBackpressure);
        Assert.NotNull(service);
    }

    [Fact]
    public void UseRedisPubSub_OnBuilder_RegistersPubSubService()
    {
        var services = new ServiceCollection();
        services.AddVapeCache()
            .UseRedisPubSub();

        Assert.Contains(
            services,
            static descriptor => descriptor.ServiceType == typeof(IRedisPubSubService));
    }

    [Fact]
    public async Task AddVapeCachePubSub_AutofacExtension_ResolvesPubSubService()
    {
        var builder = new ContainerBuilder();
        builder.RegisterGeneric(typeof(NullLogger<>))
            .As(typeof(ILogger<>))
            .SingleInstance();
        builder.RegisterInstance(new Mock<IRedisConnectionFactory>().Object)
            .As<IRedisConnectionFactory>()
            .SingleInstance();
        builder.AddVapeCachePubSub();

        using var container = builder.Build();
        await using var service = container.Resolve<IRedisPubSubService>();

        Assert.NotNull(service);
    }
}
