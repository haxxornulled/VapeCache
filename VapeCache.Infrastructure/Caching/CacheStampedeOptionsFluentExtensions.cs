using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching;

/// <summary>
/// Represents the cache stampede options fluent extensions.
/// </summary>
public static class CacheStampedeOptionsFluentExtensions
{
    /// <summary>
    /// Configures value.
    /// </summary>
    public static OptionsBuilder<CacheStampedeOptions> UseCacheStampedeProfile(
        this OptionsBuilder<CacheStampedeOptions> builder,
        CacheStampedeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Configure(options => CacheStampedeProfiles.Apply(options, profile));
        return builder;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public static OptionsBuilder<CacheStampedeOptions> ConfigureCacheStampede(
        this OptionsBuilder<CacheStampedeOptions> builder,
        Action<CacheStampedeOptionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Configure(options => configure(new CacheStampedeOptionsBuilder(options)));
        return builder;
    }
}

/// <summary>
/// Represents the cache stampede options builder.
/// </summary>
public sealed class CacheStampedeOptionsBuilder
{
    private readonly CacheStampedeOptions _options;

    internal CacheStampedeOptionsBuilder(CacheStampedeOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Configures value.
    /// </summary>
    public CacheStampedeOptionsBuilder UseProfile(CacheStampedeProfile profile)
    {
        CacheStampedeProfiles.Apply(_options, profile);
        return this;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public CacheStampedeOptionsBuilder Enabled(bool enabled = true)
    {
        _options.Enabled = enabled;
        return this;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public CacheStampedeOptionsBuilder RejectSuspiciousKeys(bool reject = true)
    {
        _options.RejectSuspiciousKeys = reject;
        return this;
    }

    /// <summary>
    /// Configures value.
    /// </summary>
    public CacheStampedeOptionsBuilder WithMaxKeys(int maxKeys)
    {
        _options.MaxKeys = maxKeys;
        return this;
    }

    /// <summary>
    /// Configures value.
    /// </summary>
    public CacheStampedeOptionsBuilder WithMaxKeyLength(int maxKeyLength)
    {
        _options.MaxKeyLength = maxKeyLength;
        return this;
    }

    /// <summary>
    /// Configures value.
    /// </summary>
    public CacheStampedeOptionsBuilder WithLockWaitTimeout(TimeSpan timeout)
    {
        _options.LockWaitTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public CacheStampedeOptionsBuilder EnableFailureBackoff(bool enabled = true)
    {
        _options.EnableFailureBackoff = enabled;
        return this;
    }

    /// <summary>
    /// Configures value.
    /// </summary>
    public CacheStampedeOptionsBuilder WithFailureBackoff(TimeSpan backoff)
    {
        _options.FailureBackoff = backoff;
        return this;
    }
}
