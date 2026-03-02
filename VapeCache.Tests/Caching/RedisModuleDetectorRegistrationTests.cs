using System.Reflection;
using System.Runtime.CompilerServices;
using Autofac;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using VapeCache.Abstractions.Modules;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;
using VapeCache.Infrastructure.DependencyInjection;

namespace VapeCache.Tests.Caching;

public sealed class RedisModuleDetectorRegistrationTests
{
    [Fact]
    public void AddVapecacheCaching_UsesRawRedisExecutor_ForModuleDetection()
    {
        var services = new ServiceCollection();
        services.AddVapecacheCaching();

        var descriptor = services.Single(d => d.ServiceType == typeof(IRedisModuleDetector));
        Assert.NotNull(descriptor.ImplementationFactory);

        var sentinel = CreateUninitializedRedisExecutor();
        var provider = new Mock<IServiceProvider>(MockBehavior.Strict);
        provider.Setup(p => p.GetService(typeof(RedisCommandExecutor))).Returns(sentinel);

        var detector = Assert.IsAssignableFrom<IRedisModuleDetector>(descriptor.ImplementationFactory!(provider.Object));

        Assert.Same(sentinel, GetExecutor(detector));
        provider.Verify(p => p.GetService(typeof(RedisCommandExecutor)), Times.Once);
        provider.VerifyNoOtherCalls();
    }

    [Fact]
    public void AutofacModule_UsesRawRedisExecutor_ForModuleDetection()
    {
        var sentinel = CreateUninitializedRedisExecutor();
        var builder = new ContainerBuilder();
        builder.RegisterModule(new VapeCacheCachingModule());
        builder.RegisterInstance(sentinel).AsSelf().SingleInstance().ExternallyOwned();

        using var container = builder.Build();
        var detector = container.Resolve<IRedisModuleDetector>();

        Assert.Same(sentinel, GetExecutor(detector));
    }

    private static RedisCommandExecutor CreateUninitializedRedisExecutor()
        => (RedisCommandExecutor)RuntimeHelpers.GetUninitializedObject(typeof(RedisCommandExecutor));

    private static object? GetExecutor(IRedisModuleDetector detector)
        => detector.GetType()
            .GetField("_executor", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(detector);
}
