using VapeCache.Application.Abstractions;
using VapeCache.Application.Caching.Invalidation.Commands;
using VapeCache.Application.Caching.Invalidation.Events;
using VapeCache.Features.Invalidation;

namespace VapeCache.Application.Caching.Invalidation.Handlers;

/// <summary>
/// Publishes direct zone invalidation commands.
/// </summary>
public sealed class InvalidateCacheZonesCommandHandler(
    ICacheInvalidationEventPublisher publisher)
    : ICommandHandler<InvalidateCacheZonesCommand, CacheInvalidationExecutionResult>
{
    private readonly ICacheInvalidationEventPublisher _publisher = publisher;

    public ValueTask<CacheInvalidationExecutionResult> HandleAsync(
        InvalidateCacheZonesCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _publisher.PublishAsync(new CacheZonesInvalidatedEvent(command.Zones), cancellationToken);
    }
}
