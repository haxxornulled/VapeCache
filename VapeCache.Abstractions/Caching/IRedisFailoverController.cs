namespace VapeCache.Abstractions.Caching;

public interface IRedisFailoverController
{
    bool IsForcedOpen { get; }
    string? Reason { get; }

    void ForceOpen(string reason);
    void ClearForcedOpen();

    // Methods for circuit breaker state management (called by hybrid executors)
    void MarkRedisSuccess();
    void MarkRedisFailure();
}

