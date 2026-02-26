using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching;

public static class CacheStampedeOptionsFluentExtensions
{
    public static OptionsBuilder<CacheStampedeOptions> UseCacheStampedeProfile(
        this OptionsBuilder<CacheStampedeOptions> builder,
        CacheStampedeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Configure(options => CacheStampedeProfiles.Apply(options, profile));
        return builder;
    }

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

public sealed class CacheStampedeOptionsBuilder
{
    private readonly CacheStampedeOptions _options;

    internal CacheStampedeOptionsBuilder(CacheStampedeOptions options)
    {
        _options = options;
    }

    public CacheStampedeOptionsBuilder UseProfile(CacheStampedeProfile profile)
    {
        CacheStampedeProfiles.Apply(_options, profile);
        return this;
    }

    public CacheStampedeOptionsBuilder Enabled(bool enabled = true)
    {
        _options.Enabled = enabled;
        return this;
    }

    public CacheStampedeOptionsBuilder RejectSuspiciousKeys(bool reject = true)
    {
        _options.RejectSuspiciousKeys = reject;
        return this;
    }

    public CacheStampedeOptionsBuilder WithMaxKeys(int maxKeys)
    {
        _options.MaxKeys = maxKeys;
        return this;
    }

    public CacheStampedeOptionsBuilder WithMaxKeyLength(int maxKeyLength)
    {
        _options.MaxKeyLength = maxKeyLength;
        return this;
    }

    public CacheStampedeOptionsBuilder WithLockWaitTimeout(TimeSpan timeout)
    {
        _options.LockWaitTimeout = timeout;
        return this;
    }

    public CacheStampedeOptionsBuilder EnableFailureBackoff(bool enabled = true)
    {
        _options.EnableFailureBackoff = enabled;
        return this;
    }

    public CacheStampedeOptionsBuilder WithFailureBackoff(TimeSpan backoff)
    {
        _options.FailureBackoff = backoff;
        return this;
    }
}
