namespace VapeCache.Features.Invalidation;

/// <summary>
/// Merges multiple policies into a single normalized invalidation plan.
/// </summary>
public sealed class CompositeInvalidationPolicy<TEvent> : ICacheInvalidationPolicy<TEvent>
{
    private readonly IReadOnlyList<ICacheInvalidationPolicy<TEvent>> _policies;

    public CompositeInvalidationPolicy(IEnumerable<ICacheInvalidationPolicy<TEvent>> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        _policies = policies as IReadOnlyList<ICacheInvalidationPolicy<TEvent>> ?? policies.ToArray();
    }

    public async ValueTask<CacheInvalidationPlan> BuildPlanAsync(TEvent eventData, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_policies.Count == 0)
            return CacheInvalidationPlan.Empty;

        var builder = new CacheInvalidationPlanBuilder();
        for (var i = 0; i < _policies.Count; i++)
        {
            var plan = await _policies[i].BuildPlanAsync(eventData, cancellationToken).ConfigureAwait(false);
            builder.AddPlan(plan);
        }

        return builder.Build();
    }
}
