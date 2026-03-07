namespace VapeCache.Features.Invalidation;

/// <summary>
/// Resolves cache invalidation targets from a domain event or command.
/// </summary>
/// <typeparam name="TEvent">Event payload type.</typeparam>
public interface ICacheInvalidationPolicy<in TEvent>
{
    /// <summary>
    /// Builds an invalidation plan for the supplied event payload.
    /// </summary>
    ValueTask<CacheInvalidationPlan> BuildPlanAsync(TEvent eventData, CancellationToken cancellationToken = default);
}
