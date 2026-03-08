using VapeCache.Features.Invalidation;

namespace VapeCache.Application.Caching.Invalidation;

/// <summary>
/// Default application publisher that routes events into the invalidation dispatcher.
/// </summary>
public sealed class CacheInvalidationEventPublisher(ICacheInvalidationDispatcher dispatcher)
    : ICacheInvalidationEventPublisher
{
    private readonly ICacheInvalidationDispatcher _dispatcher = dispatcher;

    /// <summary>
    /// Provides member behavior.
    /// </summary>
    public ValueTask<CacheInvalidationExecutionResult> PublishAsync<TEvent>(
        TEvent eventData,
        CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        return _dispatcher.DispatchAsync(eventData, cancellationToken);
    }
}
