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
public readonly record struct EfCoreQueryCacheKeyBuiltEvent
{
    public EfCoreQueryCacheKeyBuiltEvent(
        Guid CommandId,
        Guid ContextInstanceId,
        string ProviderName,
        string CacheKey,
        int CommandTextLength,
        int ParameterCount)
    {
        this.CommandId = CommandId;
        this.ContextInstanceId = ContextInstanceId;
        this.ProviderName = ProviderName;
        this.CacheKey = CacheKey;
        this.CommandTextLength = CommandTextLength;
        this.ParameterCount = ParameterCount;
    }

    public Guid CommandId { get; init; }
    public Guid ContextInstanceId { get; init; }
    public string ProviderName { get; init; }
    public string CacheKey { get; init; }
    public int CommandTextLength { get; init; }
    public int ParameterCount { get; init; }
}

/// <summary>
/// Query execution completion event for profiler correlation.
/// </summary>
public readonly record struct EfCoreQueryExecutionCompletedEvent
{
    public EfCoreQueryExecutionCompletedEvent(
        Guid CommandId,
        Guid ContextInstanceId,
        string ProviderName,
        string CacheKey,
        double DurationMs,
        bool Succeeded,
        string? FailureType,
        string? FailureMessage)
    {
        this.CommandId = CommandId;
        this.ContextInstanceId = ContextInstanceId;
        this.ProviderName = ProviderName;
        this.CacheKey = CacheKey;
        this.DurationMs = DurationMs;
        this.Succeeded = Succeeded;
        this.FailureType = FailureType;
        this.FailureMessage = FailureMessage;
    }

    public Guid CommandId { get; init; }
    public Guid ContextInstanceId { get; init; }
    public string ProviderName { get; init; }
    public string CacheKey { get; init; }
    public double DurationMs { get; init; }
    public bool Succeeded { get; init; }
    public string? FailureType { get; init; }
    public string? FailureMessage { get; init; }
}

/// <summary>
/// Invalidation plan capture event emitted at SaveChanges capture time.
/// </summary>
public readonly record struct EfCoreInvalidationPlanCapturedEvent
{
    public EfCoreInvalidationPlanCapturedEvent(Guid ContextInstanceId, IReadOnlyList<string> Zones)
    {
        this.ContextInstanceId = ContextInstanceId;
        this.Zones = Zones;
    }

    public Guid ContextInstanceId { get; init; }
    public IReadOnlyList<string> Zones { get; init; }
}

/// <summary>
/// Zone invalidation success event.
/// </summary>
public readonly record struct EfCoreZoneInvalidatedEvent
{
    public EfCoreZoneInvalidatedEvent(Guid ContextInstanceId, string Zone, long Version)
    {
        this.ContextInstanceId = ContextInstanceId;
        this.Zone = Zone;
        this.Version = Version;
    }

    public Guid ContextInstanceId { get; init; }
    public string Zone { get; init; }
    public long Version { get; init; }
}

/// <summary>
/// Zone invalidation failure event.
/// </summary>
public readonly record struct EfCoreZoneInvalidationFailedEvent
{
    public EfCoreZoneInvalidationFailedEvent(
        Guid ContextInstanceId,
        string Zone,
        string FailureType,
        string FailureMessage)
    {
        this.ContextInstanceId = ContextInstanceId;
        this.Zone = Zone;
        this.FailureType = FailureType;
        this.FailureMessage = FailureMessage;
    }

    public Guid ContextInstanceId { get; init; }
    public string Zone { get; init; }
    public string FailureType { get; init; }
    public string FailureMessage { get; init; }
}
