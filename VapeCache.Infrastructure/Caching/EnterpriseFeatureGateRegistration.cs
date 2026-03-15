using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Caching;

/// <summary>
/// Extension helpers for overriding enterprise feature gating from licensing modules.
/// </summary>
public static class EnterpriseFeatureGateRegistration
{
    /// <summary>
    /// Replaces the default enterprise feature gate with a custom implementation.
    /// </summary>
    public static IServiceCollection AddVapeCacheEnterpriseFeatureGate<TFeatureGate>(this IServiceCollection services)
        where TFeatureGate : class, IEnterpriseFeatureGate
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<IEnterpriseFeatureGate>();
        services.AddSingleton<IEnterpriseFeatureGate, TFeatureGate>();
        return services;
    }

    /// <summary>
    /// Replaces the default enterprise feature gate with a factory-backed implementation.
    /// </summary>
    public static IServiceCollection AddVapeCacheEnterpriseFeatureGate(
        this IServiceCollection services,
        Func<IServiceProvider, IEnterpriseFeatureGate> factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);

        services.RemoveAll<IEnterpriseFeatureGate>();
        services.AddSingleton(factory);
        return services;
    }
}
