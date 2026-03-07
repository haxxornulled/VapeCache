using VapeCache.Application.Caching.Invalidation.Events;
using VapeCache.Features.Invalidation;

namespace VapeCache.Application.Caching.Invalidation.Policies;

/// <summary>
/// Projects explicit key invalidation events into invalidation plans.
/// </summary>
public sealed class CacheKeysInvalidatedEventPolicy : ICacheInvalidationPolicy<CacheKeysInvalidatedEvent>
{
    public ValueTask<CacheInvalidationPlan> BuildPlanAsync(
        CacheKeysInvalidatedEvent eventData,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new CacheInvalidationPlan(keys: eventData.Keys));
    }
}
