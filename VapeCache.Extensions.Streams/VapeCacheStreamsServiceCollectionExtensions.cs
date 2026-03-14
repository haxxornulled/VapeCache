using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VapeCache.Extensions.Streams;

/// <summary>
/// Microsoft DI wiring for Redis streams extension services.
/// </summary>
public static class VapeCacheStreamsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Redis stream idempotent producer facade.
    /// </summary>
    public static IServiceCollection AddVapeCacheStreams(
        this IServiceCollection services,
        Action<RedisStreamIdempotentProducerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<RedisStreamIdempotentProducerOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton<IRedisStreamIdempotentProducer, RedisStreamIdempotentProducer>();
        return services;
    }
}
