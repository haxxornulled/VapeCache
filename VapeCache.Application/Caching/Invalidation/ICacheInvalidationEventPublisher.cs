using VapeCache.Features.Invalidation;

namespace VapeCache.Application.Caching.Invalidation;

/// <summary>
/// Application-facing abstraction for publishing cache invalidation events.
/// </summary>
public interface ICacheInvalidationEventPublisher
{
    ValueTask<CacheInvalidationExecutionResult> PublishAsync<TEvent>(
        TEvent eventData,
        CancellationToken cancellationToken = default)
        where TEvent : notnull;
}
