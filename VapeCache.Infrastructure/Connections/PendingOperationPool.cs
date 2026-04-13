using System.Diagnostics;
using System.Threading.Tasks.Sources;

namespace VapeCache.Infrastructure.Connections;

internal sealed class PendingOperationPool
{
    private readonly System.Threading.Lock _poolGate = new();
    private readonly CancellationToken _shutdownToken;
    private readonly IInFlightGate _inFlight;
    private readonly Action<long>? _recordLatencyStopwatchTicks;
    private readonly Func<bool>? _shouldRecordLatency;
    private PendingOperation? _head;

    public PendingOperationPool(
        CancellationToken shutdownToken,
        IInFlightGate inFlight,
        Action<long>? recordLatencyStopwatchTicks = null,
        Func<bool>? shouldRecordLatency = null)
    {
        _shutdownToken = shutdownToken;
        _inFlight = inFlight;
        _recordLatencyStopwatchTicks = recordLatencyStopwatchTicks;
        _shouldRecordLatency = shouldRecordLatency;
    }

    public PendingOperationPool(
        CancellationToken shutdownToken,
        SemaphoreSlim inFlight,
        Action<long>? recordLatencyStopwatchTicks = null,
        Func<bool>? shouldRecordLatency = null)
        : this(
            shutdownToken,
            new SemaphoreInFlightGateAdapter(inFlight),
            recordLatencyStopwatchTicks,
            shouldRecordLatency)
    {
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public PendingOperation Rent()
    {
        PendingOperation? op;
        lock (_poolGate)
        {
            op = _head;
            if (op is not null)
            {
                _head = op.NextPooled;
                op.NextPooled = null;
            }
        }

        if (op is not null)
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

    private void Return(PendingOperation operation)
    {
        lock (_poolGate)
        {
            operation.NextPooled = _head;
            _head = operation;
        }
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryTake(out PendingOperation? operation)
    {
        lock (_poolGate)
        {
            operation = _head;
            if (operation is null)
                return false;

            _head = operation.NextPooled;
            operation.NextPooled = null;
            return true;
        }
    }
}

internal enum OperationClass : byte
{
    Fast = 0,
    Bulk = 1
}

internal sealed class PendingOperation : IValueTaskSource<RedisRespReader.RespValue>
{
    private static readonly Action<object?> CancelByCallerCallback = static state =>
        ((PendingOperation)state!).TrySetCanceledFromCallerCallback();
    private static readonly Action<object?> CancelByShutdownCallback = static state =>
        ((PendingOperation)state!).TrySetCanceledFromShutdownCallback();

    private ManualResetValueTaskSourceCore<RedisRespReader.RespValue> _core;
    private readonly CancellationToken _shutdownToken;
    private readonly IInFlightGate _inFlight;
    private readonly Action<PendingOperation> _returnToPool;
    private CancellationTokenRegistration _ctr;
    private CancellationTokenRegistration _shutdownCtr;
    private CancellationToken _ct;
    private bool _holdsSlot;
    private readonly Action<long>? _recordLatencyStopwatchTicks;
    private readonly Func<bool>? _shouldRecordLatency;
    private int _completed;
    private int _registrationsDisposed;
    private int _responseProcessed;
    private int _awaiterObserved;
    private int _returnedToPool;
    private int _operationClass;
    private int _responseMode;
    private long _sequenceId;
    private long _generation;
    private long _startedStopwatchTicks;
    internal PendingOperation? NextPooled { get; set; }

    public PendingOperation(
        CancellationToken shutdownToken,
        IInFlightGate inFlight,
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

    public PendingOperation(
        CancellationToken shutdownToken,
        SemaphoreSlim inFlight,
        Action<PendingOperation> returnToPool,
        Action<long>? recordLatencyStopwatchTicks,
        Func<bool>? shouldRecordLatency)
        : this(
            shutdownToken,
            new SemaphoreInFlightGateAdapter(inFlight),
            returnToPool,
            recordLatencyStopwatchTicks,
            shouldRecordLatency)
    {
    }

    public bool PoolBulk { get; private set; }
    public bool IsCompleted => Volatile.Read(ref _completed) != 0;
    public ValueTask<RedisRespReader.RespValue> ValueTask { get; private set; }
    public OperationClass OperationClass => (OperationClass)Volatile.Read(ref _operationClass);
    public RedisResponseMode ResponseMode => (RedisResponseMode)Volatile.Read(ref _responseMode);
    public long SequenceId => Volatile.Read(ref _sequenceId);
    public long Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// Executes value.
    /// </summary>
    public void Reset()
    {
        PoolBulk = false;
        ValueTask = default;
        _ctr = default;
        _shutdownCtr = default;
        _ct = default;
        _holdsSlot = false;
        _core.RunContinuationsAsynchronously = true;
        _core.Reset();
        Volatile.Write(ref _completed, 0);
        Volatile.Write(ref _registrationsDisposed, 0);
        Volatile.Write(ref _responseProcessed, 0);
        Volatile.Write(ref _awaiterObserved, 0);
        Volatile.Write(ref _returnedToPool, 0);
        Volatile.Write(ref _operationClass, (int)OperationClass.Fast);
        Volatile.Write(ref _responseMode, (int)RedisResponseMode.Default);
        Volatile.Write(ref _sequenceId, 0);
        Volatile.Write(ref _generation, 0);
        Volatile.Write(ref _startedStopwatchTicks, 0);
        NextPooled = null;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public void Start(
        bool poolBulk,
        CancellationToken ct,
        bool holdsSlot,
        long sequenceId,
        OperationClass operationClass = OperationClass.Fast,
        RedisResponseMode responseMode = RedisResponseMode.Default)
    {
        PoolBulk = poolBulk;
        _ct = ct;
        _holdsSlot = holdsSlot;
        Volatile.Write(ref _operationClass, (int)operationClass);
        Volatile.Write(ref _responseMode, (int)responseMode);
        Volatile.Write(ref _sequenceId, sequenceId);
        Volatile.Write(
            ref _startedStopwatchTicks,
            _recordLatencyStopwatchTicks is not null && (_shouldRecordLatency?.Invoke() ?? true)
                ? Stopwatch.GetTimestamp()
                : 0);
        ValueTask = new ValueTask<RedisRespReader.RespValue>(this, _core.Version);

        if (ct.CanBeCanceled)
        {
            _ctr = ct.Register(CancelByCallerCallback, this);
            _shutdownCtr = _shutdownToken.Register(CancelByShutdownCallback, this);
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
        if (!TryBeginCompletion())
            return;

        DisposeRegistrationsOnce();
        TryRecordLatency();
        _core.SetResult(value);
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public void TrySetException(Exception ex)
    {
        if (!TryBeginCompletion())
            return;

        DisposeRegistrationsOnce();
        TryRecordLatency();
        _core.SetException(ex);
    }

    private bool TryBeginCompletion()
        => Interlocked.CompareExchange(ref _completed, 1, 0) == 0;

    private void DisposeRegistrationsOnce()
    {
        if (Interlocked.Exchange(ref _registrationsDisposed, 1) != 0)
            return;

        _ctr.Dispose();
        _shutdownCtr.Dispose();
    }

    private void TrySetCanceledFromCallerCallback()
    {
        var callerToken = _ct;
        if (!callerToken.CanBeCanceled || !callerToken.IsCancellationRequested)
            return;

        TrySetException(new OperationCanceledException(callerToken));
    }

    private void TrySetCanceledFromShutdownCallback()
    {
        if (!_shutdownToken.IsCancellationRequested)
            return;

        TrySetException(new OperationCanceledException());
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

        TryReturnToPool();
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public void AbortUnqueued(Exception ex)
    {
        TrySetException(ex);
        MarkResponseProcessed();
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public void AbortEnqueueFailure(bool valueTaskWillBeObserved)
    {
        TrySetException(new InvalidOperationException("Enqueue failed"));
        MarkResponseProcessed();
        if (!valueTaskWillBeObserved)
        {
            Volatile.Write(ref _awaiterObserved, 1);
            TryReturnToPool();
        }
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
        if (Interlocked.Exchange(ref _returnedToPool, 1) != 0) return;
        _returnToPool(this);
    }

    RedisRespReader.RespValue IValueTaskSource<RedisRespReader.RespValue>.GetResult(short token)
    {
        try
        {
            return _core.GetResult(token);
        }
        finally
        {
            MarkAwaiterObserved();
        }
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
