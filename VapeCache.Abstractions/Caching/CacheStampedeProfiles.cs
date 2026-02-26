namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Helpers for applying named stampede-protection presets.
/// </summary>
public static class CacheStampedeProfiles
{
    public static CacheStampedeOptions Create(CacheStampedeProfile profile)
    {
        var options = new CacheStampedeOptions();
        Apply(options, profile);
        return options;
    }

    public static void Apply(CacheStampedeOptions options, CacheStampedeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(options);

        switch (profile)
        {
            case CacheStampedeProfile.Strict:
                options.Enabled = true;
                options.MaxKeys = 25_000;
                options.RejectSuspiciousKeys = true;
                options.MaxKeyLength = 256;
                options.LockWaitTimeout = TimeSpan.FromMilliseconds(500);
                options.EnableFailureBackoff = true;
                options.FailureBackoff = TimeSpan.FromSeconds(1);
                break;

            case CacheStampedeProfile.Balanced:
                options.Enabled = true;
                options.MaxKeys = 50_000;
                options.RejectSuspiciousKeys = true;
                options.MaxKeyLength = 512;
                options.LockWaitTimeout = TimeSpan.FromMilliseconds(750);
                options.EnableFailureBackoff = true;
                options.FailureBackoff = TimeSpan.FromMilliseconds(500);
                break;

            case CacheStampedeProfile.Relaxed:
                options.Enabled = true;
                options.MaxKeys = 100_000;
                options.RejectSuspiciousKeys = true;
                options.MaxKeyLength = 1024;
                options.LockWaitTimeout = TimeSpan.FromMilliseconds(1500);
                options.EnableFailureBackoff = true;
                options.FailureBackoff = TimeSpan.FromMilliseconds(250);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown cache stampede profile.");
        }
    }
}
