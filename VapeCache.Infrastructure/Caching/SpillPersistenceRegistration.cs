using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching;

/// <summary>
/// Registration helpers for enabling disk-backed spill persistence.
/// </summary>
public static class SpillPersistenceRegistration
{
    /// <summary>
    /// Registers the segmented log spill store and diagnostics wiring.
    /// </summary>
    public static IServiceCollection AddVapeCachePersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<IInMemorySpillStore>();
        services.RemoveAll<ISpillStoreDiagnostics>();
        services.AddSingleton<SegmentedLogSpillStore>();
        services.AddSingleton<IInMemorySpillStore>(sp => sp.GetRequiredService<SegmentedLogSpillStore>());
        services.AddSingleton<ISpillStoreDiagnostics>(sp => sp.GetRequiredService<SegmentedLogSpillStore>());
        return services;
    }
}
