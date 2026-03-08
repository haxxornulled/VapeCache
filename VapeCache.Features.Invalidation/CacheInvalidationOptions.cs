namespace VapeCache.Features.Invalidation;

/// <summary>
/// Runtime options for policy-driven cache invalidation execution.
/// </summary>
public sealed class CacheInvalidationOptions
{
    private const int AbsoluteMaxConcurrency = 256;

    /// <summary>
    /// Configuration section used for binding options.
    /// </summary>
    public const string ConfigurationSectionName = "VapeCache:Invalidation";

    /// <summary>
    /// Enables invalidation execution.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Enables tag version invalidation operations.
    /// </summary>
    public bool EnableTagInvalidation { get; set; } = true;

    /// <summary>
    /// Enables zone version invalidation operations.
    /// </summary>
    public bool EnableZoneInvalidation { get; set; } = true;

    /// <summary>
    /// Enables per-key removal invalidation operations.
    /// </summary>
    public bool EnableKeyInvalidation { get; set; } = true;

    /// <summary>
    /// Profile preset that controls strictness and concurrency defaults.
    /// </summary>
    public CacheInvalidationProfile Profile { get; set; } = CacheInvalidationProfile.SmallWebsite;

    /// <summary>
    /// Optional override for whether execution failures should throw.
    /// </summary>
    public bool? ThrowOnFailure { get; set; }

    /// <summary>
    /// Optional override for whether target invalidation executes concurrently.
    /// </summary>
    public bool? ExecuteTargetsInParallel { get; set; }

    /// <summary>
    /// Optional override for whether policy evaluation executes concurrently.
    /// </summary>
    public bool? EvaluatePoliciesInParallel { get; set; }

    /// <summary>
    /// Optional override for max concurrent operations used by policy and target execution.
    /// </summary>
    public int? MaxConcurrency { get; set; }

    /// <summary>
    /// Executes resolve runtime settings.
    /// </summary>
    public CacheInvalidationRuntimeSettings ResolveRuntimeSettings()
    {
        var defaults = Profile switch
        {
            CacheInvalidationProfile.HighTrafficSite => new CacheInvalidationRuntimeSettings(
                ThrowOnFailure: true,
                ExecuteTargetsInParallel: true,
                EvaluatePoliciesInParallel: true,
                MaxConcurrency: Math.Max(4, Environment.ProcessorCount)),

            CacheInvalidationProfile.DesktopApp => new CacheInvalidationRuntimeSettings(
                ThrowOnFailure: false,
                ExecuteTargetsInParallel: false,
                EvaluatePoliciesInParallel: false,
                MaxConcurrency: 1),

            _ => new CacheInvalidationRuntimeSettings(
                ThrowOnFailure: false,
                ExecuteTargetsInParallel: false,
                EvaluatePoliciesInParallel: false,
                MaxConcurrency: 1)
        };

        var maxConcurrency = ClampConcurrency(MaxConcurrency.GetValueOrDefault(defaults.MaxConcurrency));

        return defaults with
        {
            ThrowOnFailure = ThrowOnFailure.GetValueOrDefault(defaults.ThrowOnFailure),
            ExecuteTargetsInParallel = ExecuteTargetsInParallel.GetValueOrDefault(defaults.ExecuteTargetsInParallel),
            EvaluatePoliciesInParallel = EvaluatePoliciesInParallel.GetValueOrDefault(defaults.EvaluatePoliciesInParallel),
            MaxConcurrency = maxConcurrency
        };
    }

    private static int ClampConcurrency(int value)
    {
        if (value < 1)
            return 1;

        // Cap configured fan-out to protect thread-pool health in misconfigured environments.
        var cpuScaledCap = Math.Max(4, Environment.ProcessorCount * 8);
        var hardCap = cpuScaledCap < AbsoluteMaxConcurrency ? cpuScaledCap : AbsoluteMaxConcurrency;
        return value <= hardCap ? value : hardCap;
    }
}

/// <summary>
/// Represents the struct.
/// </summary>
public readonly record struct CacheInvalidationRuntimeSettings(
    bool ThrowOnFailure,
    bool ExecuteTargetsInParallel,
    bool EvaluatePoliciesInParallel,
    int MaxConcurrency);
