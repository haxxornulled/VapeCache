using VapeCache.Application.Abstractions;
using VapeCache.Features.Invalidation;

namespace VapeCache.Application.Caching.Invalidation.Commands;

/// <summary>
/// Generic command for entity-scoped cache invalidation.
/// </summary>
public sealed record InvalidateEntityCacheCommand : ICommand<CacheInvalidationExecutionResult>
{
    public InvalidateEntityCacheCommand(
        string EntityName,
        IReadOnlyList<string> EntityIds,
        IReadOnlyList<string>? Zones = null,
        IReadOnlyList<string>? KeyPrefixes = null,
        IReadOnlyList<string>? Keys = null,
        IReadOnlyList<string>? Tags = null)
    {
        this.EntityName = EntityName;
        this.EntityIds = EntityIds;
        this.Zones = Zones;
        this.KeyPrefixes = KeyPrefixes;
        this.Keys = Keys;
        this.Tags = Tags;
    }

    /// <summary>
    /// Executes nvalidate entity cache command.
    /// </summary>
    public InvalidateEntityCacheCommand(
        string EntityName,
        string EntityId,
        IReadOnlyList<string>? Zones = null,
        IReadOnlyList<string>? KeyPrefixes = null,
        IReadOnlyList<string>? Keys = null,
        IReadOnlyList<string>? Tags = null)
        : this(EntityName, [EntityId], Zones, KeyPrefixes, Keys, Tags)
    {
    }

    public string EntityName { get; init; }
    public IReadOnlyList<string> EntityIds { get; init; }
    public IReadOnlyList<string>? Zones { get; init; }
    public IReadOnlyList<string>? KeyPrefixes { get; init; }
    public IReadOnlyList<string>? Keys { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
}
