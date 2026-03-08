using VapeCache.Application.Abstractions;
using VapeCache.Features.Invalidation;

namespace VapeCache.Application.Caching.Invalidation.Commands;

/// <summary>
/// Generic command for direct tag invalidation.
/// </summary>
public sealed record InvalidateCacheTagsCommand(IReadOnlyList<string> Tags) : ICommand<CacheInvalidationExecutionResult>
{
    /// <summary>
    /// Executes nvalidate cache tags command.
    /// </summary>
    public InvalidateCacheTagsCommand(params string[] tags)
        : this((IReadOnlyList<string>)tags)
    {
    }
}
