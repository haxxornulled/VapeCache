using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
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
}
