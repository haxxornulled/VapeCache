using VapeCache.Application.Abstractions;
using VapeCache.Application.Caching.Invalidation.Commands;
using VapeCache.Application.Caching.Invalidation.Events;
using VapeCache.Features.Invalidation;

namespace VapeCache.Application.Caching.Invalidation.Handlers;

/// <summary>
/// Publishes direct key invalidation commands.
/// </summary>
public sealed class InvalidateCacheKeysCommandHandler(
    ICacheInvalidationEventPublisher publisher)
    : ICommandHandler<InvalidateCacheKeysCommand, CacheInvalidationExecutionResult>
{
    private readonly ICacheInvalidationEventPublisher _publisher = publisher;

    public ValueTask<CacheInvalidationExecutionResult> HandleAsync(
        InvalidateCacheKeysCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _publisher.PublishAsync(new CacheKeysInvalidatedEvent(command.Keys), cancellationToken);
    }
}
