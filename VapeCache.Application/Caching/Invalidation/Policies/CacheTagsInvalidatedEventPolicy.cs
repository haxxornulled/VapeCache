using VapeCache.Application.Caching.Invalidation.Events;
using VapeCache.Features.Invalidation;

namespace VapeCache.Application.Caching.Invalidation.Policies;

/// <summary>
/// Projects explicit tag invalidation events into invalidation plans.
/// </summary>
public sealed class CacheTagsInvalidatedEventPolicy : ICacheInvalidationPolicy<CacheTagsInvalidatedEvent>
{
    public ValueTask<CacheInvalidationPlan> BuildPlanAsync(
        CacheTagsInvalidatedEvent eventData,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new CacheInvalidationPlan(tags: eventData.Tags));
    }
}
