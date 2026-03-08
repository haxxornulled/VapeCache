namespace VapeCache.Features.Invalidation;

/// <summary>
/// Out-of-the-box policy that projects an event into zone invalidation targets.
/// </summary>
public sealed class ZoneInvalidationPolicy<TEvent> : ICacheInvalidationPolicy<TEvent>
{
    private readonly Func<TEvent, IEnumerable<string>?> _zonesSelector;
    private readonly Func<TEvent, bool>? _predicate;

    /// <summary>
    /// Executes zone invalidation policy.
    /// </summary>
    public ZoneInvalidationPolicy(
        Func<TEvent, IEnumerable<string>?> zonesSelector,
        Func<TEvent, bool>? predicate = null)
    {
        _zonesSelector = zonesSelector ?? throw new ArgumentNullException(nameof(zonesSelector));
        _predicate = predicate;
    }

    /// <summary>
    /// Executes build plan async.
    /// </summary>
    public ValueTask<CacheInvalidationPlan> BuildPlanAsync(TEvent eventData, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_predicate is not null && !_predicate(eventData))
            return ValueTask.FromResult(CacheInvalidationPlan.Empty);

        return ValueTask.FromResult(new CacheInvalidationPlan(zones: _zonesSelector(eventData)));
    }
}
