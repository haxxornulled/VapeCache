using VapeCache.Application.Abstractions;
using VapeCache.Features.Invalidation;

namespace VapeCache.Application.Caching.Invalidation.Commands;

/// <summary>
/// Generic command for direct zone invalidation.
/// </summary>
public sealed record InvalidateCacheZonesCommand(IReadOnlyList<string> Zones) : ICommand<CacheInvalidationExecutionResult>
{
    public InvalidateCacheZonesCommand(params string[] zones)
        : this((IReadOnlyList<string>)zones)
    {
    }
}
