using System.Threading.Tasks;

namespace VapeCache.Infrastructure.Connections;

internal interface IInFlightGate : IDisposable
{
    int CurrentCount { get; }
    bool Wait(int millisecondsTimeout);
    bool Wait(int millisecondsTimeout, CancellationToken ct);
    ValueTask WaitAsync(CancellationToken ct);
    int Release();
}

internal sealed class AsyncInFlightGate : IInFlightGate
{
    private readonly int _maxCount;
    private readonly GateSignal _availableSignal = new();
    private int _available;
    private int _disposed;

    public AsyncInFlightGate(int initialCount, int maxCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(initialCount, maxCount);

        _available = initialCount;
        _maxCount = maxCount;
    }

    public int CurrentCount
    {
        get
        {
            ThrowIfDisposed();
            return Math.Max(0, Volatile.Read(ref _available));
        }
    }

    public bool Wait(int millisecondsTimeout)
        => Wait(millisecondsTimeout, CancellationToken.None);

    public bool Wait(int millisecondsTimeout, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        if (millisecondsTimeout == 0)
            return TryAcquire();

        var task = WaitAsync(ct);
        return task.AsTask().Wait(millisecondsTimeout, ct);
    }

    public ValueTask WaitAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        if (TryAcquire())
            return ValueTask.CompletedTask;

        return WaitAsyncSlow(ct);
    }

    public int Release()
    {
        ThrowIfDisposed();

        while (true)
        {
            var current = Volatile.Read(ref _available);
            if (current >= _maxCount)
                throw new SemaphoreFullException();

            if (Interlocked.CompareExchange(ref _available, current + 1, current) == current)
            {
                _availableSignal.Set();
                return current + 1;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _availableSignal.Dispose();
    }

    private async ValueTask WaitAsyncSlow(CancellationToken ct)
    {
        var spinner = new SpinWait();
        while (true)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            if (TryAcquire())
                return;

            if (spinner.Count < 10)
            {
                spinner.SpinOnce();
                continue;
            }

            var observedVersion = _availableSignal.Version;
            if (Volatile.Read(ref _available) > 0)
                continue;

            await _availableSignal.WaitAsync(observedVersion, ct).ConfigureAwait(false);
        }
    }

    private bool TryAcquire()
    {
        while (true)
        {
            var current = Volatile.Read(ref _available);
            if (current <= 0)
                return false;

            if (Interlocked.CompareExchange(ref _available, current - 1, current) == current)
                return true;
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

    private sealed class GateSignal : IDisposable
    {
        private volatile TaskCompletionSource<bool>? _waiters;
        private long _version;
        private int _disposed;

        public long Version => Volatile.Read(ref _version);

        public void Set()
        {
            if (Volatile.Read(ref _disposed) == 1)
                return;

            Interlocked.Increment(ref _version);
            var waiters = Interlocked.Exchange(ref _waiters, null);
            waiters?.TrySetResult(true);
        }

        public ValueTask WaitAsync(long observedVersion, CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) == 1 || Volatile.Read(ref _version) != observedVersion)
                return ValueTask.CompletedTask;

            while (true)
            {
                var current = _waiters;
                if (Volatile.Read(ref _disposed) == 1 || Volatile.Read(ref _version) != observedVersion)
                    return ValueTask.CompletedTask;

                if (current is null)
                {
                    var created = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var prior = Interlocked.CompareExchange(ref _waiters, created, null);
                    current = prior ?? created;
                    if (prior is not null)
                        continue;
                }

                if (Volatile.Read(ref _disposed) == 1 || Volatile.Read(ref _version) != observedVersion)
                    return ValueTask.CompletedTask;

                return new ValueTask(current.Task.WaitAsync(ct));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            Interlocked.Exchange(ref _waiters, null)?.TrySetCanceled();
        }
    }
}

internal sealed class SemaphoreInFlightGateAdapter : IInFlightGate
{
    private readonly SemaphoreSlim _semaphore;

    public SemaphoreInFlightGateAdapter(SemaphoreSlim semaphore)
    {
        _semaphore = semaphore;
    }

    public int CurrentCount => _semaphore.CurrentCount;
    public bool Wait(int millisecondsTimeout) => _semaphore.Wait(millisecondsTimeout);
    public bool Wait(int millisecondsTimeout, CancellationToken ct) => _semaphore.Wait(millisecondsTimeout, ct);
    public async ValueTask WaitAsync(CancellationToken ct) => await _semaphore.WaitAsync(ct).ConfigureAwait(false);
    public int Release() => _semaphore.Release();
    public void Dispose() => _semaphore.Dispose();
}
