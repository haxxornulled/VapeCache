using Autofac;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Extensions.DependencyInjection;
using VapeCache.Extensions.PubSub;
using VapeCache.Infrastructure.Caching;
using Moq;

namespace VapeCache.Tests.DependencyInjection;

public sealed class VapeCachePubSubExtensionsTests
{
    [Fact]
    public void AddVapeCachePubSub_ThrowsWhenServicesIsNull()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Throws<ArgumentNullException>(() =>
            VapeCachePubSubServiceCollectionExtensions.AddVapeCachePubSub(
                services: null!,
                configuration,
                sectionName: "RedisPubSub"));
    }

    [Fact]
    public void AddVapeCachePubSub_ThrowsWhenConfigurationIsNull()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() =>
            services.AddVapeCachePubSub(
                configuration: null!,
                sectionName: "RedisPubSub"));
    }

    [Fact]
    public void AddVapeCachePubSub_ThrowsWhenSectionNameIsWhitespace()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        Assert.Throws<ArgumentException>(() =>
            services.AddVapeCachePubSub(
                configuration,
                sectionName: "   "));
    }

    [Fact]
    public void UseRedisPubSub_ThrowsWhenBuilderIsNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            VapeCachePubSubBuilderExtensions.UseRedisPubSub(
                builder: null!));
    }

    [Fact]
    public void AddVapeCachePubSub_AutofacExtension_ThrowsWhenBuilderIsNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            VapeCachePubSubAutofacExtensions.AddVapeCachePubSub(
                builder: null!));
    }

    [Fact]
    public void PubSubRegistration_ThrowsWhenServicesIsNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PubSubRegistration.AddVapecachePubSubServices(
                services: null!));
    }

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
    public void UseRedisPubSub_OnBuilder_WithConfiguration_RegistersAndBindsOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RedisPubSub:DeliveryQueueCapacity"] = "128"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddVapeCache()
            .UseRedisPubSub(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RedisPubSubOptions>>().Value;

        Assert.Equal(128, options.DeliveryQueueCapacity);
        Assert.Contains(
            services,
            static descriptor => descriptor.ServiceType == typeof(IRedisPubSubService));
    }

    [Fact]
    public void UseRedisPubSub_OnBuilder_WithNullConfiguration_Throws()
    {
        var services = new ServiceCollection();
        var builder = services.AddVapeCache();

        Assert.Throws<ArgumentNullException>(() =>
            builder.UseRedisPubSub(configuration: null!));
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
