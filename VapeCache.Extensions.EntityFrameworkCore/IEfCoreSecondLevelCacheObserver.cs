namespace VapeCache.Extensions.EntityFrameworkCore;

/// <summary>
/// Observer hook for EF Core second-level cache interceptor events.
/// </summary>
public interface IEfCoreSecondLevelCacheObserver
{
    /// <summary>
    /// Called when a deterministic query cache key is built.
    /// </summary>
    void OnQueryCacheKeyBuilt(in EfCoreQueryCacheKeyBuiltEvent @event)
    {
    }

    /// <summary>
    /// Called when a query command execution completes and can be correlated with cache-key data.
    /// </summary>
    void OnQueryExecutionCompleted(in EfCoreQueryExecutionCompletedEvent @event)
    {
    }

    /// <summary>
    /// Called when changed entities are mapped into zone invalidation targets.
    /// </summary>
    void OnInvalidationPlanCaptured(in EfCoreInvalidationPlanCapturedEvent @event)
    {
    }

    /// <summary>
    /// Called when a zone invalidation succeeds.
    /// </summary>
    void OnZoneInvalidated(in EfCoreZoneInvalidatedEvent @event)
    {
    }

    /// <summary>
    /// Called when a zone invalidation fails.
    /// </summary>
    void OnZoneInvalidationFailed(in EfCoreZoneInvalidationFailedEvent @event)
    {
    }
}

/// <summary>
/// Event payload for query cache-key generation.
/// </summary>
/// <param name="ProviderName">EF provider name.</param>
/// <param name="CacheKey">Generated cache key.</param>
/// <param name="CommandTextLength">Length of SQL text.</param>
/// <param name="ParameterCount">Parameter count.</param>
public readonly record struct EfCoreQueryCacheKeyBuiltEvent(
    Guid CommandId,
    Guid ContextInstanceId,
    string ProviderName,
    string CacheKey,
    int CommandTextLength,
    int ParameterCount);

/// <summary>
/// Query execution completion event for profiler correlation.
/// </summary>
public readonly record struct EfCoreQueryExecutionCompletedEvent(
    Guid CommandId,
    Guid ContextInstanceId,
    string ProviderName,
    string CacheKey,
    double DurationMs,
    bool Succeeded,
    string? FailureType,
    string? FailureMessage);

/// <summary>
/// Invalidation plan capture event emitted at SaveChanges capture time.
/// </summary>
public readonly record struct EfCoreInvalidationPlanCapturedEvent(
    Guid ContextInstanceId,
    IReadOnlyList<string> Zones);

/// <summary>
/// Zone invalidation success event.
/// </summary>
public readonly record struct EfCoreZoneInvalidatedEvent(
    Guid ContextInstanceId,
    string Zone,
    long Version);

/// <summary>
/// Zone invalidation failure event.
/// </summary>
public readonly record struct EfCoreZoneInvalidationFailedEvent(
    Guid ContextInstanceId,
    string Zone,
    string FailureType,
    string FailureMessage);
