namespace VapeCache.Features.Invalidation;

/// <summary>
/// Out-of-the-box policy that projects an event into key removal targets.
/// </summary>
public sealed class KeyInvalidationPolicy<TEvent> : ICacheInvalidationPolicy<TEvent>
{
    private readonly Func<TEvent, IEnumerable<string>?> _keysSelector;
    private readonly Func<TEvent, bool>? _predicate;

    /// <summary>
    /// Executes key invalidation policy.
    /// </summary>
    public KeyInvalidationPolicy(
        Func<TEvent, IEnumerable<string>?> keysSelector,
        Func<TEvent, bool>? predicate = null)
    {
        _keysSelector = keysSelector ?? throw new ArgumentNullException(nameof(keysSelector));
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

        return ValueTask.FromResult(new CacheInvalidationPlan(keys: _keysSelector(eventData)));
    }
}
