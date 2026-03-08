namespace VapeCache.Features.Invalidation;

/// <summary>
/// Summarizes invalidation execution outcomes.
/// </summary>
public readonly record struct CacheInvalidationExecutionResult(
    int RequestedTargets,
    int InvalidatedTargets,
    int FailedTargets,
    int SkippedTargets,
    int PolicyFailures = 0)
{
    /// <summary>
    /// Defines the has failures.
    /// </summary>
    public bool HasFailures => FailedTargets > 0 || PolicyFailures > 0;
}
