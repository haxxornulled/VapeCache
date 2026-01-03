namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Configuration options for Redis reconciliation (syncing in-memory writes back to Redis after recovery).
/// </summary>
public sealed class RedisReconciliationOptions
{
    /// <summary>
    /// Whether reconciliation is enabled. When false, in-memory writes are never synced back to Redis.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum age of a tracked operation before it's considered stale and discarded.
    /// Operations older than this will not be synced to Redis.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan MaxOperationAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum number of tracked operations to keep in memory.
    /// New operations are dropped when the limit is reached.
    /// Default: 100000.
    /// </summary>
    public int MaxPendingOperations { get; set; } = 100_000;

    /// <summary>
    /// Maximum number of operations processed in a single reconciliation run.
    /// Set to 0 for unlimited.
    /// Default: 10000.
    /// </summary>
    public int MaxOperationsPerRun { get; set; } = 10_000;

    /// <summary>
    /// Batch size used during reconciliation processing.
    /// Default: 256.
    /// </summary>
    public int BatchSize { get; set; } = 256;

    /// <summary>
    /// Maximum amount of time a reconciliation run is allowed to execute.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan MaxRunDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Initial backoff applied after a failed operation.
    /// Default: 25ms.
    /// </summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Maximum backoff applied after repeated failures.
    /// Default: 2s.
    /// </summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Exponential backoff multiplier used after failures.
    /// Default: 2.0.
    /// </summary>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Maximum number of consecutive failures allowed before stopping reconciliation early.
    /// Prevents long blocking when Redis is still unhealthy. Set to 0 to disable.
    /// Default: 10.
    /// </summary>
    public int MaxConsecutiveFailures { get; set; } = 10;
}
