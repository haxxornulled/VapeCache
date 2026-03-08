using VapeCache.Application.Abstractions;
using VapeCache.Application.Caching.Invalidation.Commands;
using VapeCache.Application.Caching.Invalidation.Events;
using VapeCache.Features.Invalidation;

namespace VapeCache.Application.Caching.Invalidation.Handlers;

/// <summary>
/// Publishes direct tag invalidation commands.
/// </summary>
public sealed class InvalidateCacheTagsCommandHandler(
    ICacheInvalidationEventPublisher publisher)
    : ICommandHandler<InvalidateCacheTagsCommand, CacheInvalidationExecutionResult>
{
    private readonly ICacheInvalidationEventPublisher _publisher = publisher;

    public ValueTask<CacheInvalidationExecutionResult> HandleAsync(
        InvalidateCacheTagsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _publisher.PublishAsync(new CacheTagsInvalidatedEvent(command.Tags), cancellationToken);
    }
}
