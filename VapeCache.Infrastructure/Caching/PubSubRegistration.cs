using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Infrastructure.Caching;

/// <summary>
/// Service registration for Redis pub/sub runtime support.
/// </summary>
public static class PubSubRegistration
{
    /// <summary>
    /// Adds Redis pub/sub services and options validation.
    /// </summary>
    public static IServiceCollection AddVapecachePubSubServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<RedisPubSubOptions>()
            .ValidateOnStart();
        services.TryAddSingleton<RedisPubSubOptionsValidator>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<RedisPubSubOptions>, RedisPubSubOptionsValidator>());
        services.TryAddSingleton<IRedisPubSubService, RedisPubSubService>();

        return services;
    }
}
