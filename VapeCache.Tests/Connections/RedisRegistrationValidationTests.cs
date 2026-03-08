using Autofac;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;
using VapeCache.Infrastructure.DependencyInjection;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.Connections;

public sealed class RedisRegistrationValidationTests
{
    [Fact]
    public async Task ServiceRegistrations_FailHostStartup_WhenRedisEndpointIsMissing()
    {
        using var provider = BuildServiceProvider();
        var validator = GetStartupValidator(provider, "RedisConnectionOptionsStartupHostedService");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => validator.StartAsync(CancellationToken.None));
        var validation = AssertOptionsValidationFailure(ex);

        Assert.Contains("RedisConnection:Host or RedisConnection:ConnectionString is required.", validation.Failures);
    }

    [Fact]
    public async Task ServiceRegistrations_FailHostStartup_WhenMultiplexerOptionsAreInvalid()
    {
        using var provider = BuildServiceProvider(new Dictionary<string, string?>
        {
            ["RedisConnection:Host"] = "redis.internal",
            ["RedisMultiplexer:Connections"] = "0"
        });
        var validator = GetStartupValidator(provider, "RedisMultiplexerOptionsStartupHostedService");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => validator.StartAsync(CancellationToken.None));
        var validation = AssertOptionsValidationFailure(ex);

        Assert.Contains("RedisMultiplexer:Connections must be > 0.", validation.Failures);
    }

    [Fact]
    public async Task ServiceRegistrations_Start_WhenRedisEndpointIsConfigured()
    {
        using var provider = BuildServiceProvider(new Dictionary<string, string?>
        {
            ["RedisConnection:Host"] = "redis.internal"
        });

        var connectionValidator = GetStartupValidator(provider, "RedisConnectionOptionsStartupHostedService");
        var multiplexerValidator = GetStartupValidator(provider, "RedisMultiplexerOptionsStartupHostedService");

        await connectionValidator.StartAsync(CancellationToken.None);
        await multiplexerValidator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ServiceRegistrations_FailHostStartup_WhenAutoscaleConnectionsAreOutOfRange()
    {
        using var provider = BuildServiceProvider(new Dictionary<string, string?>
        {
            ["RedisConnection:Host"] = "redis.internal",
            ["RedisMultiplexer:EnableAutoscaling"] = "true",
            ["RedisMultiplexer:MinConnections"] = "4",
            ["RedisMultiplexer:MaxConnections"] = "8",
            ["RedisMultiplexer:Connections"] = "2"
        });
        var validator = GetStartupValidator(provider, "RedisMultiplexerOptionsStartupHostedService");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => validator.StartAsync(CancellationToken.None));
        var validation = AssertOptionsValidationFailure(ex);

        Assert.Contains("RedisMultiplexer:Connections must be within [MinConnections, MaxConnections] when autoscaling is enabled.", validation.Failures);
    }

    [Fact]
    public async Task ServiceRegistrations_FailHostStartup_WhenBulkLaneIsolationIsMisconfigured()
    {
        using var provider = BuildServiceProvider(new Dictionary<string, string?>
        {
            ["RedisConnection:Host"] = "redis.internal",
            ["RedisMultiplexer:Connections"] = "1",
            ["RedisMultiplexer:BulkLaneConnections"] = "1"
        });
        var validator = GetStartupValidator(provider, "RedisMultiplexerOptionsStartupHostedService");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => validator.StartAsync(CancellationToken.None));
        var validation = AssertOptionsValidationFailure(ex);

        Assert.Contains("RedisMultiplexer:BulkLaneConnections requires RedisMultiplexer:Connections > 1 for lane isolation.", validation.Failures);
    }

    [Fact]
    public async Task ServiceRegistrations_FailHostStartup_WhenAutoAdjustBulkLaneRatioIsInvalid()
    {
        using var provider = BuildServiceProvider(new Dictionary<string, string?>
        {
            ["RedisConnection:Host"] = "redis.internal",
            ["RedisMultiplexer:Connections"] = "8",
            ["RedisMultiplexer:AutoAdjustBulkLanes"] = "true",
            ["RedisMultiplexer:BulkLaneTargetRatio"] = "1.2"
        });
        var validator = GetStartupValidator(provider, "RedisMultiplexerOptionsStartupHostedService");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => validator.StartAsync(CancellationToken.None));
        var validation = AssertOptionsValidationFailure(ex);

        Assert.Contains("RedisMultiplexer:BulkLaneTargetRatio must be in [0,0.90] when AutoAdjustBulkLanes is enabled.", validation.Failures);
    }

    [Fact]
    public void AutofacConnectionsModule_FailsContainerBuild_WhenRedisEndpointIsMissing()
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule(new VapeCacheConnectionsModule());

        var ex = Assert.ThrowsAny<Exception>(() => builder.Build());

        var validation = AssertOptionsValidationFailure(ex);
        Assert.Contains("RedisConnection:Host or RedisConnection:ConnectionString is required.", validation.Failures);
    }

    [Fact]
    public void AutofacConnectionsModule_Builds_WhenRedisEndpointIsConfigured()
    {
        var builder = new ContainerBuilder();
        var options = new TestOptionsMonitor<RedisConnectionOptions>(new RedisConnectionOptions
        {
            Host = "redis.internal"
        });

        builder.RegisterInstance(options)
            .As<IOptionsMonitor<RedisConnectionOptions>>()
            .SingleInstance();
        builder.RegisterModule(new VapeCacheConnectionsModule());

        using var container = builder.Build();

        Assert.NotNull(container);
    }

    [Fact]
    public void RedisConnectionOptions_DisablePasswordOnlyAuthFallback_ByDefault()
    {
        var options = new RedisConnectionOptions();

        Assert.False(options.AllowAuthFallbackToPasswordOnly);
    }

    private static ServiceProvider BuildServiceProvider(IReadOnlyDictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder();
        if (settings is not null)
        {
            configuration.AddInMemoryCollection(settings);
        }

        var root = configuration.Build();
        var services = new ServiceCollection();
        services.AddVapecacheRedisConnections();
        services.AddVapecacheCaching();
        services.AddOptions<RedisConnectionOptions>()
            .Bind(root.GetSection("RedisConnection"));
        services.AddOptions<RedisMultiplexerOptions>()
            .Bind(root.GetSection("RedisMultiplexer"));
        return services.BuildServiceProvider();
    }

    private static OptionsValidationException AssertOptionsValidationFailure(Exception ex)
        => Assert.IsType<OptionsValidationException>(ex.GetBaseException());

    private static IHostedService GetStartupValidator(IServiceProvider provider, string typeName)
        => provider.GetServices<IHostedService>().Single(service => service.GetType().Name == typeName);
}
