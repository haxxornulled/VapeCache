namespace VapeCache.Features.Invalidation;

/// <summary>
/// Out-of-the-box policy that projects an event into tag invalidation targets.
/// </summary>
public sealed class TagInvalidationPolicy<TEvent> : ICacheInvalidationPolicy<TEvent>
{
    private readonly Func<TEvent, IEnumerable<string>?> _tagsSelector;
    private readonly Func<TEvent, bool>? _predicate;

    public TagInvalidationPolicy(
        Func<TEvent, IEnumerable<string>?> tagsSelector,
        Func<TEvent, bool>? predicate = null)
    {
        _tagsSelector = tagsSelector ?? throw new ArgumentNullException(nameof(tagsSelector));
        _predicate = predicate;
    }

    public ValueTask<CacheInvalidationPlan> BuildPlanAsync(TEvent eventData, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_predicate is not null && !_predicate(eventData))
            return ValueTask.FromResult(CacheInvalidationPlan.Empty);

        return ValueTask.FromResult(new CacheInvalidationPlan(tags: _tagsSelector(eventData)));
    }
}
