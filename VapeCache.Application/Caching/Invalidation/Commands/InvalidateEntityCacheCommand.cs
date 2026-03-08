using VapeCache.Application.Abstractions;
using VapeCache.Features.Invalidation;

namespace VapeCache.Application.Caching.Invalidation.Commands;

/// <summary>
/// Generic command for entity-scoped cache invalidation.
/// </summary>
public sealed record InvalidateEntityCacheCommand(
    string EntityName,
    IReadOnlyList<string> EntityIds,
    IReadOnlyList<string>? Zones = null,
    IReadOnlyList<string>? KeyPrefixes = null,
    IReadOnlyList<string>? Keys = null,
    IReadOnlyList<string>? Tags = null) : ICommand<CacheInvalidationExecutionResult>
{
    /// <summary>
    /// Executes nvalidate entity cache command.
    /// </summary>
    public InvalidateEntityCacheCommand(
        string entityName,
        string entityId,
        IReadOnlyList<string>? zones = null,
        IReadOnlyList<string>? keyPrefixes = null,
        IReadOnlyList<string>? keys = null,
        IReadOnlyList<string>? tags = null)
        : this(entityName, [entityId], zones, keyPrefixes, keys, tags)
    {
    }
}
