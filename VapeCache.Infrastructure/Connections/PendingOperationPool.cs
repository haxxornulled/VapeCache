using System.Diagnostics;
using System.Collections.Concurrent;
using System.Threading.Tasks.Sources;

namespace VapeCache.Infrastructure.Connections;

internal sealed class PendingOperationPool
{
    private readonly ConcurrentBag<PendingOperation> _pool = new();
    private readonly CancellationToken _shutdownToken;
    private readonly SemaphoreSlim _inFlight;
    private readonly Action<long>? _recordLatencyStopwatchTicks;
    private readonly Func<bool>? _shouldRecordLatency;

    public PendingOperationPool(
        CancellationToken shutdownToken,
        SemaphoreSlim inFlight,
        Action<long>? recordLatencyStopwatchTicks = null,
        Func<bool>? shouldRecordLatency = null)
    {
        _shutdownToken = shutdownToken;
        _inFlight = inFlight;
        _recordLatencyStopwatchTicks = recordLatencyStopwatchTicks;
        _shouldRecordLatency = shouldRecordLatency;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public PendingOperation Rent()
    {
        if (_pool.TryTake(out var op))
        {
            op.Reset();
            return op;
        }

        return new PendingOperation(
            _shutdownToken,
            _inFlight,
            Return,
            _recordLatencyStopwatchTicks,
            _shouldRecordLatency);
    }

    private void Return(PendingOperation operation) => _pool.Add(operation);

    /// <summary>
    /// Attempts to value.
    /// </summary>
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
    private readonly Action<long>? _recordLatencyStopwatchTicks;
    private readonly Func<bool>? _shouldRecordLatency;
    private int _completed;
    private int _responseProcessed;
    private int _awaiterObserved;
    private long _sequenceId;
    private long _generation;
    private long _startedStopwatchTicks;

    public PendingOperation(
        CancellationToken shutdownToken,
        SemaphoreSlim inFlight,
        Action<PendingOperation> returnToPool,
        Action<long>? recordLatencyStopwatchTicks,
        Func<bool>? shouldRecordLatency)
    {
        _shutdownToken = shutdownToken;
        _inFlight = inFlight;
        _returnToPool = returnToPool;
        _recordLatencyStopwatchTicks = recordLatencyStopwatchTicks;
        _shouldRecordLatency = shouldRecordLatency;
        _core = new ManualResetValueTaskSourceCore<RedisRespReader.RespValue>
        {
            RunContinuationsAsynchronously = true
        };
    }

    public bool PoolBulk { get; private set; }
    public bool IsCompleted => Volatile.Read(ref _completed) != 0;
    public ValueTask<RedisRespReader.RespValue> ValueTask { get; private set; }
    public long SequenceId => Volatile.Read(ref _sequenceId);
    public long Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// Executes value.
    /// </summary>
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
        Volatile.Write(ref _sequenceId, 0);
        Volatile.Write(ref _generation, 0);
        Volatile.Write(ref _startedStopwatchTicks, 0);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public void Start(bool poolBulk, CancellationToken ct, bool holdsSlot, long sequenceId)
    {
        PoolBulk = poolBulk;
        _ct = ct;
        _holdsSlot = holdsSlot;
        Volatile.Write(ref _sequenceId, sequenceId);
        Volatile.Write(
            ref _startedStopwatchTicks,
            _recordLatencyStopwatchTicks is not null && (_shouldRecordLatency?.Invoke() ?? true)
                ? Stopwatch.GetTimestamp()
                : 0);
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

    public void AssignSequenceId(long sequenceId)
        => Volatile.Write(ref _sequenceId, sequenceId);

    public void AssignGeneration(long generation)
        => Volatile.Write(ref _generation, generation);

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public void TrySetResult(RedisRespReader.RespValue value)
    {
        if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
            return;

        _ctr.Dispose();
        _shutdownCtr.Dispose();
        TryRecordLatency();
        _core.SetResult(value);
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public void TrySetException(Exception ex)
    {
        if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
            return;

        _ctr.Dispose();
        _shutdownCtr.Dispose();
        TryRecordLatency();
        _core.SetException(ex);
    }

    private void TryRecordLatency()
    {
        var startedTicks = Volatile.Read(ref _startedStopwatchTicks);
        if (startedTicks <= 0 || _recordLatencyStopwatchTicks is null)
            return;

        var elapsedTicks = Stopwatch.GetTimestamp() - startedTicks;
        if (elapsedTicks > 0)
            _recordLatencyStopwatchTicks(elapsedTicks);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public void MarkResponseProcessed()
    {
        Volatile.Write(ref _responseProcessed, 1);
        if (_holdsSlot)
        {
            _holdsSlot = false;
            _inFlight.Release();
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public void AbortUnqueued(Exception ex)
    {
        TrySetException(ex);
        Volatile.Write(ref _awaiterObserved, 1);
        MarkResponseProcessed();
    }

    /// <summary>
    /// Executes value.
    /// </summary>
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

    /// <summary>
    /// Executes value.
    /// </summary>
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
