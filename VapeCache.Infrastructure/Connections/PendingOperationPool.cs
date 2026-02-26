using System.Collections.Concurrent;
using System.Threading.Tasks.Sources;

namespace VapeCache.Infrastructure.Connections;

internal sealed class PendingOperationPool
{
    private readonly ConcurrentBag<PendingOperation> _pool = new();
    private readonly CancellationToken _shutdownToken;
    private readonly SemaphoreSlim _inFlight;

    public PendingOperationPool(CancellationToken shutdownToken, SemaphoreSlim inFlight)
    {
        _shutdownToken = shutdownToken;
        _inFlight = inFlight;
    }

    public PendingOperation Rent()
    {
        if (_pool.TryTake(out var op))
        {
            op.Reset();
            return op;
        }

        return new PendingOperation(_shutdownToken, _inFlight, Return);
    }

    private void Return(PendingOperation operation) => _pool.Add(operation);

    public bool TryTake(out PendingOperation? operation) => _pool.TryTake(out operation);
}

internal sealed class PendingOperation : IValueTaskSource<RedisRespReader.RespValue>
{
    private ManualResetValueTaskSourceCore<RedisRespReader.RespValue> _core;
    private readonly CancellationToken _shutdownToken;
    private readonly SemaphoreSlim _inFlight;
    private readonly Action<PendingOperation> _returnToPool;
    private CancellationTokenRegistration _ctr;
    private CancellationTokenRegistration _shutdownCtr;
    private CancellationToken _ct;
    private bool _holdsSlot;
    private int _completed;
    private int _responseProcessed;
    private int _awaiterObserved;

    public PendingOperation(CancellationToken shutdownToken, SemaphoreSlim inFlight, Action<PendingOperation> returnToPool)
    {
        _shutdownToken = shutdownToken;
        _inFlight = inFlight;
        _returnToPool = returnToPool;
        _core = new ManualResetValueTaskSourceCore<RedisRespReader.RespValue>
        {
            RunContinuationsAsynchronously = true
        };
    }

    public bool PoolBulk { get; private set; }
    public bool IsCompleted => Volatile.Read(ref _completed) != 0;
    public ValueTask<RedisRespReader.RespValue> ValueTask { get; private set; }

    public void Reset()
    {
        PoolBulk = false;
        ValueTask = default;
        _ct = default;
        _holdsSlot = false;
        _core.RunContinuationsAsynchronously = true;
        _core.Reset();
        Volatile.Write(ref _completed, 0);
        Volatile.Write(ref _responseProcessed, 0);
        Volatile.Write(ref _awaiterObserved, 0);
    }

    public void Start(bool poolBulk, CancellationToken ct, bool holdsSlot)
    {
        PoolBulk = poolBulk;
        _ct = ct;
        _holdsSlot = holdsSlot;
        ValueTask = new ValueTask<RedisRespReader.RespValue>(this, _core.Version);

        if (ct.CanBeCanceled)
        {
            _ctr = ct.Register(static s =>
            {
                var op = (PendingOperation)s!;
                op.TrySetException(new OperationCanceledException(op._ct));
            }, this);
            _shutdownCtr = _shutdownToken.Register(static s =>
            {
                var op = (PendingOperation)s!;
                op.TrySetException(new OperationCanceledException());
            }, this);
        }
        else
        {
            // Hot path for uncancelable operations: avoid per-op registration allocations.
            _ctr = default;
            _shutdownCtr = default;
        }
    }

    public void TrySetResult(RedisRespReader.RespValue value)
    {
        if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
            return;

        _ctr.Dispose();
        _shutdownCtr.Dispose();
        _core.SetResult(value);
    }

    public void TrySetException(Exception ex)
    {
        if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
            return;

        _ctr.Dispose();
        _shutdownCtr.Dispose();
        _core.SetException(ex);
    }

    public void MarkResponseProcessed()
    {
        Volatile.Write(ref _responseProcessed, 1);
        if (_holdsSlot)
        {
            _holdsSlot = false;
            _inFlight.Release();
        }
    }

    public void AbortUnqueued(Exception ex)
    {
        TrySetException(ex);
        Volatile.Write(ref _awaiterObserved, 1);
        MarkResponseProcessed();
    }

    public void AbortEnqueueFailure()
    {
        TrySetException(new InvalidOperationException("Enqueue failed"));
        Volatile.Write(ref _responseProcessed, 1);
        Volatile.Write(ref _awaiterObserved, 1);
        if (_holdsSlot)
        {
            _holdsSlot = false;
            _inFlight.Release();
        }
        _returnToPool(this);
    }

    private void MarkAwaiterObserved()
    {
        Volatile.Write(ref _awaiterObserved, 1);
        TryReturnToPool();
    }

    private void TryReturnToPool()
    {
        if (Volatile.Read(ref _responseProcessed) == 0) return;
        if (Volatile.Read(ref _awaiterObserved) == 0) return;
        _returnToPool(this);
    }

    RedisRespReader.RespValue IValueTaskSource<RedisRespReader.RespValue>.GetResult(short token)
    {
        var result = _core.GetResult(token);
        MarkAwaiterObserved();
        return result;
    }

    ValueTaskSourceStatus IValueTaskSource<RedisRespReader.RespValue>.GetStatus(short token)
        => _core.GetStatus(token);

    void IValueTaskSource<RedisRespReader.RespValue>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);

    public void DisposeResources()
    {
        try { _ctr.Dispose(); } catch { }
        try { _shutdownCtr.Dispose(); } catch { }

        if (_holdsSlot)
        {
            try
            {
                _holdsSlot = false;
                _inFlight.Release();
            }
            catch { }
        }
    }
}
