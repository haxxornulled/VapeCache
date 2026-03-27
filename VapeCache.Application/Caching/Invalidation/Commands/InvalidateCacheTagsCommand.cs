using VapeCache.Application.Abstractions;
using VapeCache.Features.Invalidation;

namespace VapeCache.Application.Caching.Invalidation.Commands;

/// <summary>
/// Generic command for direct tag invalidation.
/// </summary>
public sealed record InvalidateCacheTagsCommand : ICommand<CacheInvalidationExecutionResult>
{
    public InvalidateCacheTagsCommand(IReadOnlyList<string> Tags)
    {
        this.Tags = Tags;
    }

    /// <summary>
    /// Executes nvalidate cache tags command.
    /// </summary>
    public InvalidateCacheTagsCommand(params string[] Tags)
        : this((IReadOnlyList<string>)Tags)
    {
    }

    public IReadOnlyList<string> Tags { get; init; }
}
