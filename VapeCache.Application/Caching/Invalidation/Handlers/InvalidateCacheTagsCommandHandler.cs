using VapeCache.Application.Abstractions;
using VapeCache.Application.Caching.Invalidation.Commands;
using VapeCache.Application.Caching.Invalidation.Events;
using VapeCache.Features.Invalidation;

namespace VapeCache.Application.Caching.Invalidation.Handlers;

/// <summary>
/// Publishes direct tag invalidation commands.
/// </summary>
public sealed class InvalidateCacheTagsCommandHandler
    : ICommandHandler<InvalidateCacheTagsCommand, CacheInvalidationExecutionResult>
{
    private readonly ICacheInvalidationEventPublisher _publisher;

    public InvalidateCacheTagsCommandHandler(ICacheInvalidationEventPublisher publisher)
    {
        _publisher = publisher;
    }

    /// <summary>
    /// Executes handle async.
    /// </summary>
    public ValueTask<CacheInvalidationExecutionResult> HandleAsync(
        InvalidateCacheTagsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _publisher.PublishAsync(new CacheTagsInvalidatedEvent(command.Tags), cancellationToken);
    }
}
