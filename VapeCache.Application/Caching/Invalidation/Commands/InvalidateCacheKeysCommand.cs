using VapeCache.Application.Abstractions;
using VapeCache.Features.Invalidation;

namespace VapeCache.Application.Caching.Invalidation.Commands;

/// <summary>
/// Generic command for direct key invalidation.
/// </summary>
public sealed record InvalidateCacheKeysCommand : ICommand<CacheInvalidationExecutionResult>
{
    public InvalidateCacheKeysCommand(IReadOnlyList<string> Keys)
    {
        this.Keys = Keys;
    }

    /// <summary>
    /// Executes nvalidate cache keys command.
    /// </summary>
    public InvalidateCacheKeysCommand(params string[] Keys)
        : this((IReadOnlyList<string>)Keys)
    {
    }

    public IReadOnlyList<string> Keys { get; init; }
}
