using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
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
    public void WithHealthChecks_RegistersHealthChecks()
    {
        var hostBuilder = new HostApplicationBuilder();
        var builder = hostBuilder.AddVapeCache();

        builder.WithHealthChecks();

        using var provider = hostBuilder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>();

        Assert.Contains(options.Value.Registrations, r => r.Name == "redis" && r.Factory is not null);
        Assert.Contains(options.Value.Registrations, r => r.Name == "vapecache" && r.Factory is not null);
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
        var builder = hostBuilder.AddVapeCache()
            .WithAspNetCoreOutputCaching(
                configureStore: store =>
                {
                    store.DefaultTtl = TimeSpan.FromSeconds(45);
                    store.KeyPrefix = "test:output";
                });

        Assert.NotNull(builder);

        await using var provider = hostBuilder.Services.BuildServiceProvider();
        var store = provider.GetRequiredService<IOutputCacheStore>();
        var options = provider.GetRequiredService<IOptionsMonitor<VapeCacheOutputCacheStoreOptions>>().CurrentValue;

        Assert.IsType<VapeCacheOutputCacheStore>(store);
        Assert.Equal("test:output", options.KeyPrefix);
        Assert.Equal(TimeSpan.FromSeconds(45), options.DefaultTtl);
    }
}
