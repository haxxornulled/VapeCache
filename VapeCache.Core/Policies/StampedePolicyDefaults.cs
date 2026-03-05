namespace VapeCache.Core.Policies;

/// <summary>
/// Domain policy defaults for cache stampede mitigation profiles.
/// </summary>
public static class StampedePolicyDefaults
{
    public static StampedeProfileSettings Strict => new(
        Enabled: true,
        MaxKeys: 25_000,
        RejectSuspiciousKeys: true,
        MaxKeyLength: 256,
        LockWaitTimeout: TimeSpan.FromMilliseconds(500),
        EnableFailureBackoff: true,
        FailureBackoff: TimeSpan.FromSeconds(1));

    public static StampedeProfileSettings Balanced => new(
        Enabled: true,
        MaxKeys: 50_000,
        RejectSuspiciousKeys: true,
        MaxKeyLength: 512,
        LockWaitTimeout: TimeSpan.FromMilliseconds(750),
        EnableFailureBackoff: true,
        FailureBackoff: TimeSpan.FromMilliseconds(500));

    public static StampedeProfileSettings Relaxed => new(
        Enabled: true,
        MaxKeys: 100_000,
        RejectSuspiciousKeys: true,
        MaxKeyLength: 1024,
        LockWaitTimeout: TimeSpan.FromMilliseconds(1500),
        EnableFailureBackoff: true,
        FailureBackoff: TimeSpan.FromMilliseconds(250));
}
