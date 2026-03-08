using VapeCache.Application.Caching.Invalidation.Events;
using VapeCache.Features.Invalidation;

namespace VapeCache.Application.Caching.Invalidation.Policies;

/// <summary>
/// Projects explicit zone invalidation events into invalidation plans.
/// </summary>
public sealed class CacheZonesInvalidatedEventPolicy : ICacheInvalidationPolicy<CacheZonesInvalidatedEvent>
{
    public ValueTask<CacheInvalidationPlan> BuildPlanAsync(
        CacheZonesInvalidatedEvent eventData,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new CacheInvalidationPlan(zones: eventData.Zones));
    }
}
