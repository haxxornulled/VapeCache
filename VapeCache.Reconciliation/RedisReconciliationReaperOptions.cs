namespace VapeCache.Reconciliation;

/// <summary>
/// Configuration options for the RedisReconciliationReaper background service.
/// Controls how frequently the reaper runs to sync pending operations back to Redis.
/// </summary>
public sealed class RedisReconciliationReaperOptions
{
    /// <summary>
    /// Enable or disable the Reaper background service.
    /// Default: true
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often the Reaper runs reconciliation.
    /// Default: 30 seconds
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Initial delay before the first reconciliation run.
    /// Useful to allow application startup to complete before starting reconciliation.
    /// Default: 10 seconds
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(10);
}
