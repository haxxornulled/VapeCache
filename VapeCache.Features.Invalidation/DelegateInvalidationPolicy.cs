namespace VapeCache.Features.Invalidation;

/// <summary>
/// Delegate-based invalidation policy for quick event projection.
/// </summary>
public sealed class DelegateInvalidationPolicy<TEvent> : ICacheInvalidationPolicy<TEvent>
{
    private readonly Func<TEvent, bool>? _predicate;
    private readonly Func<TEvent, IEnumerable<string>?>? _tagSelector;
    private readonly Func<TEvent, IEnumerable<string>?>? _zoneSelector;
    private readonly Func<TEvent, IEnumerable<string>?>? _keySelector;

    public DelegateInvalidationPolicy(
        Func<TEvent, IEnumerable<string>?>? tagSelector = null,
        Func<TEvent, IEnumerable<string>?>? zoneSelector = null,
        Func<TEvent, IEnumerable<string>?>? keySelector = null,
        Func<TEvent, bool>? predicate = null)
    {
        _tagSelector = tagSelector;
        _zoneSelector = zoneSelector;
        _keySelector = keySelector;
        _predicate = predicate;
    }

    public ValueTask<CacheInvalidationPlan> BuildPlanAsync(TEvent eventData, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_predicate is not null && !_predicate(eventData))
            return ValueTask.FromResult(CacheInvalidationPlan.Empty);

        var tags = _tagSelector?.Invoke(eventData);
        var zones = _zoneSelector?.Invoke(eventData);
        var keys = _keySelector?.Invoke(eventData);
        return ValueTask.FromResult(new CacheInvalidationPlan(tags, zones, keys));
    }
}
