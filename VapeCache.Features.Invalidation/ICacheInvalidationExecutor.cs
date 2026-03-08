namespace VapeCache.Features.Invalidation;

/// <summary>
/// Applies invalidation plans against a concrete cache instance.
/// </summary>
public interface ICacheInvalidationExecutor
{
    /// <summary>
    /// Executes nvalidate async.
    /// </summary>
    ValueTask<CacheInvalidationExecutionResult> InvalidateAsync(
        CacheInvalidationPlan plan,
        CancellationToken cancellationToken = default);
}
