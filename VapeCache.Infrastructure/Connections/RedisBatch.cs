using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisBatch : IRedisBatch
{
    private readonly IRedisCommandExecutor _executor;
    private readonly List<Task> _pending = new();

    public RedisBatch(IRedisCommandExecutor executor)
    {
        _executor = executor;
    }

    public ValueTask QueueAsync(Func<IRedisCommandExecutor, CancellationToken, ValueTask> operation, CancellationToken ct = default)
    {
        var task = operation(_executor, ct);
        if (!task.IsCompletedSuccessfully)
            _pending.Add(task.AsTask());
        return task;
    }

    public ValueTask<T> QueueAsync<T>(Func<IRedisCommandExecutor, CancellationToken, ValueTask<T>> operation, CancellationToken ct = default)
    {
        var task = operation(_executor, ct);
        if (!task.IsCompletedSuccessfully)
            _pending.Add(task.AsTask());
        return task;
    }

    public async ValueTask ExecuteAsync(CancellationToken ct = default)
    {
        if (_pending.Count == 0)
            return;

        var tasks = _pending.ToArray();
        _pending.Clear();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _pending.Clear();
        return ValueTask.CompletedTask;
    }
}
