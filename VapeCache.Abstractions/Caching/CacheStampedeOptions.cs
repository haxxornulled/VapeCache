namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Represents the cache stampede options.
/// </summary>
public sealed record CacheStampedeOptions
{
    /// <summary>
    /// Gets or sets the enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Gets or sets the max keys.
    /// </summary>
    public int MaxKeys { get; set; } = 50_000;

    /// <summary>
    /// Reject null/empty, control-character, or overly long keys to reduce cache pollution risk.
    /// </summary>
    public bool RejectSuspiciousKeys { get; set; } = true;

    /// <summary>
    /// Maximum accepted cache key length when stampede protection is enabled.
    /// </summary>
    public int MaxKeyLength { get; set; } = 512;

    /// <summary>
    /// Optional upper bound for waiting on a per-key single-flight lock.
    /// Set to TimeSpan.Zero to disable lock-wait timeout.
    /// </summary>
    public TimeSpan LockWaitTimeout { get; set; } = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// When enabled, failed factory executions trigger a short cooldown to prevent origin hammering.
    /// </summary>
    public bool EnableFailureBackoff { get; set; } = true;

    /// <summary>
    /// Cooldown duration after a factory failure for a given key.
    /// </summary>
    public TimeSpan FailureBackoff { get; set; } = TimeSpan.FromMilliseconds(500);
}
