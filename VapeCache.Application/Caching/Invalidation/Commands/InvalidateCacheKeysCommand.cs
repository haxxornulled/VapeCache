using VapeCache.Application.Abstractions;
using VapeCache.Features.Invalidation;

namespace VapeCache.Application.Caching.Invalidation.Commands;

/// <summary>
/// Generic command for direct key invalidation.
/// </summary>
public sealed record InvalidateCacheKeysCommand(IReadOnlyList<string> Keys) : ICommand<CacheInvalidationExecutionResult>
{
    public InvalidateCacheKeysCommand(params string[] keys)
        : this((IReadOnlyList<string>)keys)
    {
    }
}
