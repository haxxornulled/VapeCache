namespace VapeCache.Features.Invalidation;

/// <summary>
/// Resolves all registered policies for an event and executes the resulting plan.
/// </summary>
public interface ICacheInvalidationDispatcher
{
    /// <summary>
    /// Provides member behavior.
    /// </summary>
    ValueTask<CacheInvalidationExecutionResult> DispatchAsync<TEvent>(
        TEvent eventData,
        CancellationToken cancellationToken = default);
}
