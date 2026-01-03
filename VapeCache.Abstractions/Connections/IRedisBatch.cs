namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Client-side batch for pipelining multiple Redis operations.
/// Operations are started immediately and awaited together via ExecuteAsync.
/// </summary>
public interface IRedisBatch : IAsyncDisposable
{
    /// <summary>Queue a non-returning operation.</summary>
    ValueTask QueueAsync(Func<IRedisCommandExecutor, CancellationToken, ValueTask> operation, CancellationToken ct = default);

    /// <summary>Queue an operation and return its task for awaiting.</summary>
    ValueTask<T> QueueAsync<T>(Func<IRedisCommandExecutor, CancellationToken, ValueTask<T>> operation, CancellationToken ct = default);

    /// <summary>Await all queued operations.</summary>
    ValueTask ExecuteAsync(CancellationToken ct = default);
}
