namespace VapeCache.Abstractions.Caching;

using VapeCache.Core.Policies;

/// <summary>
/// Helpers for applying named stampede-protection presets.
/// </summary>
public static class CacheStampedeProfiles
{
    /// <summary>
    /// Creates stampede options using a named profile.
    /// </summary>
    public static CacheStampedeOptions Create(CacheStampedeProfile profile)
    {
        var options = new CacheStampedeOptions();
        Apply(options, profile);
        return options;
    }

    /// <summary>
    /// Applies a named profile to an existing options instance.
    /// </summary>
    public static void Apply(CacheStampedeOptions options, CacheStampedeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = profile switch
        {
            CacheStampedeProfile.Strict => StampedePolicyDefaults.Strict,
            CacheStampedeProfile.Balanced => StampedePolicyDefaults.Balanced,
            CacheStampedeProfile.Relaxed => StampedePolicyDefaults.Relaxed,
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown cache stampede profile.")
        };

        options.Enabled = settings.Enabled;
        options.MaxKeys = settings.MaxKeys;
        options.RejectSuspiciousKeys = settings.RejectSuspiciousKeys;
        options.MaxKeyLength = settings.MaxKeyLength;
        options.LockWaitTimeout = settings.LockWaitTimeout;
        options.EnableFailureBackoff = settings.EnableFailureBackoff;
        options.FailureBackoff = settings.FailureBackoff;
    }
}
