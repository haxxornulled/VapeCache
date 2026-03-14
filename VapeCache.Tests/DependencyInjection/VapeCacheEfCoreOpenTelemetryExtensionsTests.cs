using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VapeCache.Extensions.EntityFrameworkCore;
using VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry;

namespace VapeCache.Tests.DependencyInjection;

public sealed class VapeCacheEfCoreOpenTelemetryExtensionsTests
{
    [Fact]
    public void AddVapeCacheEfCoreOpenTelemetry_registers_observer_and_enables_callbacks()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVapeCacheEntityFrameworkCore();
        services.AddVapeCacheEfCoreOpenTelemetry();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        var observers = provider.GetServices<IEfCoreSecondLevelCacheObserver>().ToArray();
        Assert.Contains(observers, observer => observer is EfCoreOpenTelemetryObserver);

        var efOptions = provider.GetRequiredService<IOptions<EfCoreSecondLevelCacheOptions>>().Value;
        Assert.True(efOptions.EnableObserverCallbacks);
    }

    [Fact]
    public void AddVapeCacheEfCoreOpenTelemetry_allows_option_overrides()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVapeCacheEntityFrameworkCore();
        services.AddVapeCacheEfCoreOpenTelemetry(options =>
        {
            options.Enabled = false;
            options.EmitActivities = false;
        });

        using var provider = services.BuildServiceProvider(validateScopes: true);
        var otelOptions = provider.GetRequiredService<IOptions<EfCoreOpenTelemetryOptions>>().Value;
        Assert.False(otelOptions.Enabled);
        Assert.False(otelOptions.EmitActivities);
    }
}
