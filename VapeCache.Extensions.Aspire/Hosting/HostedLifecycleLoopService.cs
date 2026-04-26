using Microsoft.Extensions.Hosting;

namespace VapeCache.Extensions.Aspire.Hosting;

internal abstract class HostedLifecycleLoopService : IHostedLifecycleService, IDisposable
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _stoppingCts;
    private Task? _executingTask;

    public virtual Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_executingTask is not null)
                return Task.CompletedTask;

            _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _executingTask = ExecuteAsync(_stoppingCts.Token);
            return _executingTask.IsCompleted ? _executingTask : Task.CompletedTask;
        }
    }

    public virtual Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? executingTask;
        CancellationTokenSource? stoppingCts;

        lock (_gate)
        {
            executingTask = _executingTask;
            stoppingCts = _stoppingCts;
        }

        if (executingTask is null)
            return;

        stoppingCts?.Cancel();
        _ = await Task.WhenAny(executingTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);

        lock (_gate)
        {
            if (!ReferenceEquals(_executingTask, executingTask))
                return;

            _executingTask = null;
            _stoppingCts?.Dispose();
            _stoppingCts = null;
        }
    }

    public virtual Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual void Dispose()
    {
        lock (_gate)
        {
            _stoppingCts?.Cancel();
            _stoppingCts?.Dispose();
            _stoppingCts = null;
            _executingTask = null;
        }
    }

    protected abstract Task ExecuteAsync(CancellationToken stoppingToken);
}
