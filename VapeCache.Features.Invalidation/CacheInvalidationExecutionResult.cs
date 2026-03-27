namespace VapeCache.Features.Invalidation;

/// <summary>
/// Summarizes invalidation execution outcomes.
/// </summary>
public readonly record struct CacheInvalidationExecutionResult
{
    public CacheInvalidationExecutionResult(
        int RequestedTargets,
        int InvalidatedTargets,
        int FailedTargets,
        int SkippedTargets,
        int PolicyFailures = 0)
    {
        this.RequestedTargets = RequestedTargets;
        this.InvalidatedTargets = InvalidatedTargets;
        this.FailedTargets = FailedTargets;
        this.SkippedTargets = SkippedTargets;
        this.PolicyFailures = PolicyFailures;
    }

    public int RequestedTargets { get; init; }
    public int InvalidatedTargets { get; init; }
    public int FailedTargets { get; init; }
    public int SkippedTargets { get; init; }
    public int PolicyFailures { get; init; }

    /// <summary>
    /// Defines the has failures.
    /// </summary>
    public bool HasFailures => FailedTargets > 0 || PolicyFailures > 0;
}
