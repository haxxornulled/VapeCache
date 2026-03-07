namespace VapeCache.Features.Invalidation;

/// <summary>
/// Applies invalidation plans against a concrete cache instance.
/// </summary>
public interface ICacheInvalidationExecutor
{
    ValueTask<CacheInvalidationExecutionResult> InvalidateAsync(
        CacheInvalidationPlan plan,
        CancellationToken cancellationToken = default);
}
