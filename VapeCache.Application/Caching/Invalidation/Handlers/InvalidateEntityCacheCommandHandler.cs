using VapeCache.Application.Abstractions;
using VapeCache.Application.Caching.Invalidation.Commands;
using VapeCache.Application.Caching.Invalidation.Events;
using VapeCache.Features.Invalidation;

namespace VapeCache.Application.Caching.Invalidation.Handlers;

/// <summary>
/// Publishes generic entity cache invalidation commands as domain invalidation events.
/// </summary>
public sealed class InvalidateEntityCacheCommandHandler(
    ICacheInvalidationEventPublisher publisher)
    : ICommandHandler<InvalidateEntityCacheCommand, CacheInvalidationExecutionResult>
{
    private readonly ICacheInvalidationEventPublisher _publisher = publisher;

    public ValueTask<CacheInvalidationExecutionResult> HandleAsync(
        InvalidateEntityCacheCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.EntityName))
            throw new ArgumentException("EntityName is required.", nameof(command));

        var eventData = new EntityCacheChangedEvent(
            command.EntityName,
            command.EntityIds,
            command.Zones,
            command.KeyPrefixes,
            command.Keys,
            command.Tags);

        return _publisher.PublishAsync(eventData, cancellationToken);
    }
}
