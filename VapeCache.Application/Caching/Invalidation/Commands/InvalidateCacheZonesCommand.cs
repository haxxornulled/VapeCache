using VapeCache.Application.Abstractions;
using VapeCache.Features.Invalidation;

namespace VapeCache.Application.Caching.Invalidation.Commands;

/// <summary>
/// Generic command for direct zone invalidation.
/// </summary>
public sealed record InvalidateCacheZonesCommand : ICommand<CacheInvalidationExecutionResult>
{
    public InvalidateCacheZonesCommand(IReadOnlyList<string> Zones)
    {
        this.Zones = Zones;
    }

    /// <summary>
    /// Executes nvalidate cache zones command.
    /// </summary>
    public InvalidateCacheZonesCommand(params string[] Zones)
        : this((IReadOnlyList<string>)Zones)
    {
    }

    public IReadOnlyList<string> Zones { get; init; }
}
