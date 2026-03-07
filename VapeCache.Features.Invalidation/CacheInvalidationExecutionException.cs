namespace VapeCache.Features.Invalidation;

/// <summary>
/// Thrown when invalidation is configured to fail on execution errors.
/// </summary>
public sealed class CacheInvalidationExecutionException : Exception
{
    public CacheInvalidationExecutionResult Result { get; }

    public CacheInvalidationExecutionException(string message, CacheInvalidationExecutionResult result)
        : base(message)
    {
        Result = result;
    }
}
