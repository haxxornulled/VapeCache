namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Configuration options for Redis reconciliation (syncing in-memory writes back to Redis after recovery).
/// </summary>
public sealed record RedisReconciliationOptions
{
    /// <summary>
    /// Whether reconciliation is enabled. When false, in-memory writes are never synced back to Redis.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Maximum age of a tracked operation before it's considered stale and discarded.
    /// Operations older than this will not be synced to Redis.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan MaxOperationAge { get; init; } = TimeSpan.FromMinutes(5);
}
