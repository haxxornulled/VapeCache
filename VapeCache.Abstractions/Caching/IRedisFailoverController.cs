namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Defines the redis failover controller contract.
/// </summary>
public interface IRedisFailoverController
{
    /// <summary>
    /// Gets the s forced open.
    /// </summary>
    bool IsForcedOpen { get; }
    /// <summary>
    /// Gets the reason.
    /// </summary>
    string? Reason { get; }

    /// <summary>
    /// Executes force open.
    /// </summary>
    void ForceOpen(string reason);
    /// <summary>
    /// Executes clear forced open.
    /// </summary>
    void ClearForcedOpen();

    // Methods for circuit breaker state management (called by hybrid executors)
    /// <summary>
    /// Executes mark redis success.
    /// </summary>
    void MarkRedisSuccess();
    /// <summary>
    /// Executes mark redis failure.
    /// </summary>
    void MarkRedisFailure();
}

