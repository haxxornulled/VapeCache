namespace VapeCache.Features.Invalidation;

/// <summary>
/// Thrown when invalidation is configured to fail on execution errors.
/// </summary>
public sealed class CacheInvalidationExecutionException : Exception
{
    /// <summary>
    /// Gets the result.
    /// </summary>
    public CacheInvalidationExecutionResult Result { get; }

    /// <summary>
    /// Executes cache invalidation execution exception.
    /// </summary>
    public CacheInvalidationExecutionException(string message, CacheInvalidationExecutionResult result)
        : base(message)
    {
        Result = result;
    }
}
