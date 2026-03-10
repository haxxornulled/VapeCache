using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Extensions.DependencyInjection;

/// <summary>
/// Fluent DI builder for VapeCache runtime composition.
/// </summary>
public sealed class VapeCacheDependencyInjectionBuilder
{
    /// <summary>
    /// Gets the service collection.
    /// </summary>
    public IServiceCollection Services { get; }

    internal VapeCacheDependencyInjectionBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>
    /// Binds runtime options from configuration sections.
    /// </summary>
    public VapeCacheDependencyInjectionBuilder BindFromConfiguration(
        IConfiguration configuration,
        Action<VapeCacheConfigurationBindingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new VapeCacheConfigurationBindingOptions();
        configure?.Invoke(options);

        if (options.BindRedisConnection && !string.IsNullOrWhiteSpace(options.RedisConnectionSectionName))
            Services.AddOptions<RedisConnectionOptions>().Bind(configuration.GetSection(options.RedisConnectionSectionName));

        if (options.BindRedisMultiplexer && !string.IsNullOrWhiteSpace(options.RedisMultiplexerSectionName))
            Services.AddOptions<RedisMultiplexerOptions>().Bind(configuration.GetSection(options.RedisMultiplexerSectionName));

        if (options.BindRedisCircuitBreaker && !string.IsNullOrWhiteSpace(options.RedisCircuitBreakerSectionName))
            Services.AddOptions<RedisCircuitBreakerOptions>().Bind(configuration.GetSection(options.RedisCircuitBreakerSectionName));

        if (options.BindHybridFailover && !string.IsNullOrWhiteSpace(options.HybridFailoverSectionName))
            Services.AddOptions<HybridFailoverOptions>().Bind(configuration.GetSection(options.HybridFailoverSectionName));

        if (options.BindCacheStampede && !string.IsNullOrWhiteSpace(options.CacheStampedeSectionName))
            Services.AddOptions<CacheStampedeOptions>().Bind(configuration.GetSection(options.CacheStampedeSectionName));

        return this;
    }

    /// <summary>
    /// Applies a named cache stampede profile with optional fluent overrides.
    /// </summary>
    public VapeCacheDependencyInjectionBuilder WithCacheStampedeProfile(
        CacheStampedeProfile profile,
        Action<CacheStampedeOptionsBuilder>? configure = null)
    {
        var optionsBuilder = Services
            .AddOptions<CacheStampedeOptions>()
            .UseCacheStampedeProfile(profile);

        if (configure is not null)
            optionsBuilder.ConfigureCacheStampede(configure);

        return this;
    }
}
