namespace VapeCache.Features.Invalidation;

/// <summary>
/// Returns the same invalidation plan for every event.
/// </summary>
public sealed class StaticInvalidationPolicy<TEvent> : ICacheInvalidationPolicy<TEvent>
{
    private readonly CacheInvalidationPlan _plan;

    public StaticInvalidationPolicy(CacheInvalidationPlan plan)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
    }

    public ValueTask<CacheInvalidationPlan> BuildPlanAsync(TEvent eventData, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_plan);
    }
}
