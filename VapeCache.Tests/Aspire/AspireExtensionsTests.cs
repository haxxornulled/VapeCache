using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.OutputCaching;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Extensions.AspNetCore;
using VapeCache.Extensions.Aspire;

namespace VapeCache.Tests.Aspire;

public sealed class AspireExtensionsTests
{
    [Fact]
    public void AddVapeCache_RegistersCoreServices()
    {
        var hostBuilder = new HostApplicationBuilder();
        var builder = hostBuilder.AddVapeCache();

        Assert.NotNull(builder);
        Assert.Contains(hostBuilder.Services, sd => sd.ServiceType == typeof(IRedisCommandExecutor));
        Assert.Contains(hostBuilder.Services, sd => sd.ServiceType == typeof(ICacheService));
    }

    [Fact]
    public void AddVapeCacheClientBuilder_CanSkipCoreServiceCollectionRegistrations()
    {
        var hostBuilder = new HostApplicationBuilder();
        var builder = hostBuilder.AddVapeCacheClientBuilder(registerCoreServices: false);

        Assert.NotNull(builder);
        Assert.DoesNotContain(hostBuilder.Services, sd => sd.ServiceType == typeof(IRedisCommandExecutor));
        Assert.DoesNotContain(hostBuilder.Services, sd => sd.ServiceType == typeof(ICacheService));
        Assert.Contains(hostBuilder.Services, sd => sd.ServiceType == typeof(IVapeCacheStartupReadiness));
    }

    [Fact]
    public void WithHealthChecks_RegistersHealthChecks()
    {
        var hostBuilder = new HostApplicationBuilder();
        var builder = hostBuilder.AddVapeCache();

        builder.WithHealthChecks();

        using var provider = hostBuilder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>();

        Assert.Contains(options.Value.Registrations, r => r.Name == "redis" && r.Factory is not null);
        Assert.Contains(options.Value.Registrations, r => r.Name == "vapecache-startup-readiness" && r.Factory is not null);
        Assert.Contains(options.Value.Registrations, r => r.Name == "vapecache" && r.Factory is not null);
    }

    [Fact]
    public void UseTransport_AppliesExpectedProfileAndMuxToggles()
    {
        var hostBuilder = new HostApplicationBuilder();
        hostBuilder.AddVapeCacheClientBuilder(registerCoreServices: false)
            .UseTransport(VapeCacheAspireTransportMode.UltraLowLatency);

        using var provider = hostBuilder.Services.BuildServiceProvider();
        var connection = provider.GetRequiredService<IOptions<RedisConnectionOptions>>().Value;
        var multiplexer = provider.GetRequiredService<IOptions<RedisMultiplexerOptions>>().Value;

        Assert.Equal(RedisTransportProfile.LowLatency, connection.TransportProfile);
        Assert.Equal(RedisTransportProfile.LowLatency, multiplexer.TransportProfile);
        Assert.True(multiplexer.EnableCoalescedSocketWrites);
        Assert.True(multiplexer.EnableSocketRespReader);
        Assert.False(multiplexer.UseDedicatedLaneWorkers);
    }

    [Fact]
    public async Task WithStartupWarmup_RegistersHostedServiceAndOptions()
    {
        var hostBuilder = new HostApplicationBuilder();
        hostBuilder.AddVapeCacheClientBuilder(registerCoreServices: false)
            .WithStartupWarmup(options =>
            {
                options.ConnectionsToWarm = 6;
                options.RequiredSuccessfulConnections = 3;
                options.Timeout = TimeSpan.FromSeconds(10);
            });

        await using var provider = hostBuilder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<VapeCacheStartupWarmupOptions>>().Value;

        Assert.True(options.Enabled);
        Assert.Equal(6, options.ConnectionsToWarm);
        Assert.Equal(3, options.RequiredSuccessfulConnections);
        Assert.Equal(TimeSpan.FromSeconds(10), options.Timeout);
        Assert.Contains(
            hostBuilder.Services,
            static descriptor =>
                descriptor.ServiceType == typeof(IHostedService) &&
                descriptor.ImplementationType?.Name == "VapeCacheStartupWarmupHostedService");
    }

    [Fact]
    public void VapeCacheEndpointOptions_DefaultsToSecureOptInSurface()
    {
        var options = new VapeCacheEndpointOptions();

        Assert.False(options.Enabled);
        Assert.False(options.IncludeIntentEndpoints);
        Assert.False(options.EnableLiveStream);
        Assert.False(options.EnableDashboard);
        Assert.False(options.IncludeBreakerControlEndpoints);
    }

    [Fact]
    public void WithRedisFromAspire_ValidatesInputs()
    {
        Assert.Throws<ArgumentNullException>(() => AspireRedisResourceExtensions.WithRedisFromAspire(null!, "redis"));

        var hostBuilder = new HostApplicationBuilder();
        var builder = hostBuilder.AddVapeCache();
        Assert.Throws<ArgumentException>(() => builder.WithRedisFromAspire(""));
    }

    [Fact]
    public void WithRedisFromAspire_BindsConnectionStringFromAspireResource()
    {
        var hostBuilder = new HostApplicationBuilder();
        hostBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:redis"] = "redis://localhost:6379"
        });
        hostBuilder.AddVapeCache()
            .WithRedisFromAspire("redis");

        using var provider = hostBuilder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RedisConnectionOptions>>().Value;

        Assert.Equal("redis://localhost:6379", options.ConnectionString);
    }

    [Fact]
    public void WithRedisFromAspire_DoesNotOverrideExplicitRedisConnectionString()
    {
        var hostBuilder = new HostApplicationBuilder();
        hostBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:redis"] = "redis://aspire:6379",
            ["RedisConnection:ConnectionString"] = "redis://explicit:6379"
        });
        hostBuilder.AddVapeCache()
            .WithRedisFromAspire("redis");

        using var provider = hostBuilder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RedisConnectionOptions>>().Value;

        Assert.Equal("redis://explicit:6379", options.ConnectionString);
    }

    [Fact]
    public void WithAspireTelemetry_ReturnsBuilder()
    {
        var hostBuilder = new HostApplicationBuilder();
        var builder = hostBuilder.AddVapeCache();

        var result = builder.WithAspireTelemetry();

        Assert.Same(builder, result);
    }

    [Fact]
    public void WithAspireTelemetry_AllowsCustomTelemetryWrapperConfiguration()
    {
        var hostBuilder = new HostApplicationBuilder();
        var builder = hostBuilder.AddVapeCache();
        var metricsConfigured = false;
        var tracingConfigured = false;

        var result = builder.WithAspireTelemetry(options =>
        {
            options.UseSeqAsDefaultExporter = false;
            options.ConfigureMetrics = _ => metricsConfigured = true;
            options.ConfigureTracing = _ => tracingConfigured = true;
        });

        Assert.Same(builder, result);
        Assert.True(metricsConfigured);
        Assert.True(tracingConfigured);
    }

    [Fact]
    public void WithAspireTelemetry_InvalidEndpoint_Throws()
    {
        var hostBuilder = new HostApplicationBuilder();
        var builder = hostBuilder.AddVapeCache();

        Assert.Throws<ArgumentException>(() =>
            builder.WithAspireTelemetry(options => options.OtlpEndpoint = "not-a-uri"));
    }

    [Fact]
    public void WithAspireTelemetry_AllowsFluentSeqConfiguration()
    {
        var hostBuilder = new HostApplicationBuilder();
        var builder = hostBuilder.AddVapeCache();
        var metricsConfigured = false;
        var tracingConfigured = false;

        var result = builder.WithAspireTelemetry(options =>
        {
            options.UseSeq("http://localhost:5341", "dev-key")
                .AddMetricsConfiguration(_ => metricsConfigured = true)
                .AddTracingConfiguration(_ => tracingConfigured = true);
        });

        Assert.Same(builder, result);
        Assert.True(metricsConfigured);
        Assert.True(tracingConfigured);
    }

    [Fact]
    public async Task WithAspNetCoreOutputCaching_RegistersVapeCacheStore()
    {
        var hostBuilder = new HostApplicationBuilder();
        hostBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RedisConnection:Host"] = "redis.internal"
        });
        var builder = hostBuilder.AddVapeCache()
            .WithAspNetCoreOutputCaching(
                configureStore: store =>
                {
                    store.DefaultTtl = TimeSpan.FromSeconds(45);
                    store.KeyPrefix = "test:output";
                });
        hostBuilder.Services.AddOptions<RedisConnectionOptions>()
            .Bind(hostBuilder.Configuration.GetSection("RedisConnection"));

        Assert.NotNull(builder);

        await using var provider = hostBuilder.Services.BuildServiceProvider();
        var store = provider.GetRequiredService<IOutputCacheStore>();
        var options = provider.GetRequiredService<IOptionsMonitor<VapeCacheOutputCacheStoreOptions>>().CurrentValue;

        Assert.IsType<VapeCacheOutputCacheStore>(store);
        Assert.Equal("test:output", options.KeyPrefix);
        Assert.Equal(TimeSpan.FromSeconds(45), options.DefaultTtl);
    }
}
