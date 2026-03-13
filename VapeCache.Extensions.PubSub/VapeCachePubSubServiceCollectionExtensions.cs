using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VapeCache.Abstractions.Connections;
using VapeCache.Guards;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Extensions.PubSub;

/// <summary>
/// IServiceCollection extensions for the optional Redis pub/sub capability.
/// </summary>
public static class VapeCachePubSubServiceCollectionExtensions
{
    /// <summary>
    /// Registers Redis pub/sub services.
    /// </summary>
    public static IServiceCollection AddVapeCachePubSub(this IServiceCollection services)
        => PubSubRegistration.AddVapecachePubSubServices(services);

    /// <summary>
    /// Registers Redis pub/sub services and binds options from configuration.
    /// </summary>
    public static IServiceCollection AddVapeCachePubSub(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "RedisPubSub")
    {
        ParanoiaThrowGuard.Against.NotNull(services);
        ParanoiaThrowGuard.Against.NotNull(configuration);
        sectionName = ParanoiaThrowGuard.Against.NotNullOrWhiteSpace(sectionName);

        services.AddOptions<RedisPubSubOptions>()
            .Bind(configuration.GetSection(sectionName));

        return PubSubRegistration.AddVapecachePubSubServices(services);
    }
}
