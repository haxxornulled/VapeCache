namespace VapeCache.Extensions.DependencyInjection;

/// <summary>
/// Controls configuration section binding when adding VapeCache through the DI facade.
/// </summary>
public sealed class VapeCacheConfigurationBindingOptions
{
    /// <summary>
    /// Gets or sets the Redis connection options section name.
    /// </summary>
    public string RedisConnectionSectionName { get; set; } = "RedisConnection";

    /// <summary>
    /// Gets or sets the Redis multiplexer options section name.
    /// </summary>
    public string RedisMultiplexerSectionName { get; set; } = "RedisMultiplexer";

    /// <summary>
    /// Gets or sets the circuit-breaker options section name.
    /// </summary>
    public string RedisCircuitBreakerSectionName { get; set; } = "RedisCircuitBreaker";

    /// <summary>
    /// Gets or sets the hybrid failover options section name.
    /// </summary>
    public string HybridFailoverSectionName { get; set; } = "HybridFailover";

    /// <summary>
    /// Gets or sets the stampede options section name.
    /// </summary>
    public string CacheStampedeSectionName { get; set; } = "CacheStampede";

    /// <summary>
    /// Gets or sets a value indicating whether Redis connection options are bound.
    /// </summary>
    public bool BindRedisConnection { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Redis multiplexer options are bound.
    /// </summary>
    public bool BindRedisMultiplexer { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Redis circuit-breaker options are bound.
    /// </summary>
    public bool BindRedisCircuitBreaker { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether hybrid failover options are bound.
    /// </summary>
    public bool BindHybridFailover { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether cache stampede options are bound.
    /// </summary>
    public bool BindCacheStampede { get; set; } = true;
}
