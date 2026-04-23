using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks.Sources;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisMultiplexedConnection : IAsyncDisposable
{
    private const int InFlightSpinAttempts = 24;

    private static int _nextConnectionId;
    private readonly IRedisConnectionFactory _factory;
    private readonly int _maxInFlight;
    private readonly bool _coalesceWrites;
    private readonly bool _useSocketReader;
    private readonly bool _useDedicatedLaneWorkers;
    private readonly int _bulkMgetKeyThreshold;
    private readonly int _bulkPayloadBytesThreshold;
    private readonly int _maxBulkStringBytes;
    private readonly int _maxArrayDepth;
    private readonly TimeSpan _responseTimeout;
    private readonly int _connectionId;
    private readonly KeyValuePair<string, object?>[] _writeQueueWaitTags;
    private readonly bool _runtimeTelemetryRegistered;
    private readonly Func<bool>? _shouldRecordRuntimeTelemetry;

    private readonly MpscRingQueue<PendingRequest> _writes;
    private readonly SpscRingQueue<PendingOperation> _pending;

    private readonly AsyncInFlightGate _inFlight;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly PendingOperationPool _operationPool;
    private readonly ResponseReaderLoop _responseReaderLoop;

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writer;
    private readonly Task _reader;
    private static readonly ReadOnlyMemory<byte> CrlfMemory = "\r\n"u8.ToArray();

    private readonly CoalescedWriteDispatcher _coalescedWriteDispatcher;

    private IRedisConnection? _conn;
    private RedisRespReaderState? _respReader;
    private RedisRespSocketReaderState? _respSocketReader;
    private int _disposed;
    private long _responseTimeoutCount;
    private long _failureCount;
    private int _consecutiveFailures;
    private long _queueWaitEwmaMicrosQ8;
    private int _recentTimeoutPenalty;
    private int _recentFailurePenalty;
    private long _laneBytesSent;
    private long _laneBytesReceived;
    private long _laneOperationsStarted;
    private long _laneResponsesObserved;
    private long _laneOrphanedResponses;
    private long _laneResponseSequenceMismatches;
    private long _laneTransportResetCount;
    private long _laneExpectedResponseSequence;
    private long _lanePendingSequenceAssigned;
    private long _generation;


    private static int RoundUpToPowerOfTwo(int value)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Value must be positive");
        if (value > (1 << 30))
            throw new ArgumentOutOfRangeException(nameof(value), "Value too large to round to power of two");

        value = Math.Max(2, value);
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
    }

    public RedisMultiplexedConnection(
        IRedisConnectionFactory factory,
        int maxInFlight,
        bool coalesceWrites,
        bool enableSocketRespReader = false,
        bool useDedicatedLaneWorkers = false,
        int maxBulkStringBytes = 16 * 1024 * 1024,
        int maxArrayDepth = 64,
        TimeSpan responseTimeout = default,
        int coalescedWriteMaxBytes = 1024 * 1024,
        int coalescedWriteMaxSegments = 256,
        int coalescedWriteSmallCopyThresholdBytes = 2048,
        bool enableAdaptiveCoalescing = true,
        int adaptiveCoalescingLowDepth = 4,
        int adaptiveCoalescingHighDepth = 64,
        int adaptiveCoalescingMinWriteBytes = 64 * 1024,
        int adaptiveCoalescingMinSegments = 64,
        int adaptiveCoalescingMinSmallCopyThresholdBytes = 512,
        int coalescingEnterQueueDepth = 8,
        int coalescingExitQueueDepth = 3,
        int coalescedWriteMaxOperations = 128,
        int coalescingSpinBudget = 8,
        int bulkMgetKeyThreshold = 32,
        int bulkPayloadBytesThreshold = 64 * 1024,
        int fastTimeoutResetThreshold = 3,
        TimeSpan fastTimeoutResetWindow = default,
        Action<long>? recordLatencyStopwatchTicks = null,
        Func<bool>? shouldRecordLatency = null,
        Func<bool>? shouldRecordRuntimeTelemetry = null)
    {
        _factory = factory;
        _maxInFlight = Math.Max(1, maxInFlight);
        _coalesceWrites = coalesceWrites;
        _useSocketReader = enableSocketRespReader;
        _useDedicatedLaneWorkers = useDedicatedLaneWorkers;
        _bulkMgetKeyThreshold = Math.Max(1, bulkMgetKeyThreshold);
        _bulkPayloadBytesThreshold = Math.Max(1, bulkPayloadBytesThreshold);
        _shouldRecordRuntimeTelemetry = shouldRecordRuntimeTelemetry;
        _ = fastTimeoutResetThreshold;
        _ = fastTimeoutResetWindow;
        _maxBulkStringBytes = maxBulkStringBytes;
        _maxArrayDepth = maxArrayDepth;
        _responseTimeout = responseTimeout <= TimeSpan.Zero || responseTimeout == Timeout.InfiniteTimeSpan
            ? TimeSpan.Zero
            : responseTimeout;
        _inFlight = new AsyncInFlightGate(_maxInFlight, _maxInFlight);
        _operationPool = new PendingOperationPool(_cts.Token, _inFlight, recordLatencyStopwatchTicks, shouldRecordLatency);
        _responseReaderLoop = new ResponseReaderLoop(
            _useSocketReader,
            _responseTimeout,
            _cts.Token,
            () => _respSocketReader,
            () => _respReader,
            IsFatalSocket,
            ex => FailTransportAsync(ex));

        var capacity = RoundUpToPowerOfTwo(Math.Max(128, _maxInFlight * 4));
        _writes = new MpscRingQueue<PendingRequest>(capacity);
        _pending = new SpscRingQueue<PendingOperation>(capacity);
        _coalescedWriteDispatcher = new CoalescedWriteDispatcher(
            coalescedWriteMaxBytes,
            coalescedWriteMaxSegments,
            coalescedWriteSmallCopyThresholdBytes,
            enableAdaptiveCoalescing,
            adaptiveCoalescingLowDepth,
            adaptiveCoalescingHighDepth,
            adaptiveCoalescingMinWriteBytes,
            adaptiveCoalescingMinSegments,
            adaptiveCoalescingMinSmallCopyThresholdBytes,
            CrlfMemory,
            _writes.TryDequeue,
            () => _writes.Count,
            _pending.EnqueueAsync,
            NextPendingResponseSequence,
            ReturnHeaderBufferFromMux,
            ReturnPayloadArrayFromMux,
            static (req, ex) => req.Op.AbortUnqueued(ex),
            RecordLaneBytesSent,
            coalescingEnterQueueDepth,
            coalescingExitQueueDepth,
            coalescingSpinBudget);

        _connectionId = Interlocked.Increment(ref _nextConnectionId);
        if (IsRuntimeTelemetryEnabled())
        {
            _writeQueueWaitTags = RedisMetrics.CreateWriteQueueWaitTags(_connectionId);
            RedisTelemetry.RegisterQueueDepthProvider(_connectionId, GetQueueDepthSnapshot);
            RedisTelemetry.RegisterMuxLaneUsageProvider(_connectionId, GetMuxLaneUsageTelemetrySnapshot);
            _runtimeTelemetryRegistered = true;
        }
        else
        {
            _writeQueueWaitTags = Array.Empty<KeyValuePair<string, object?>>();
            _runtimeTelemetryRegistered = false;
        }

        _writer = _useDedicatedLaneWorkers
            ? StartLongRunningWorker(WriterLoopAsync)
            : Task.Run(WriterLoopAsync);
        _reader = _useDedicatedLaneWorkers
            ? StartLongRunningWorker(ReaderLoopAsync)
            : Task.Run(ReaderLoopAsync);
    }

    private static Task StartLongRunningWorker(Func<Task> loop)
        => Task.Factory.StartNew(
                loop,
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default)
            .Unwrap();

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<RedisRespReader.RespValue> ExecuteAsync(ReadOnlyMemory<byte> command, CancellationToken ct) =>
        ExecuteAsync(command, poolBulk: false, ct, RedisResponseMode.Default);

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<RedisRespReader.RespValue> ExecuteAsync(
        ReadOnlyMemory<byte> command,
        bool poolBulk,
        CancellationToken ct,
        RedisResponseMode responseMode = RedisResponseMode.Default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        if (TryAcquireInFlightWithSpin(ct))
            return EnqueueAfterSlot(command, poolBulk, ct, responseMode);

        return EnqueueAfterSlotAsync(command, poolBulk, ct, responseMode);
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryExecuteAsync(
        ReadOnlyMemory<byte> command,
        bool poolBulk,
        CancellationToken ct,
        out ValueTask<RedisRespReader.RespValue> task,
        RedisResponseMode responseMode = RedisResponseMode.Default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        if (!_inFlight.Wait(0))
        {
            task = default;
            return false;
        }

        return TryEnqueueAfterSlot(command, poolBulk, ct, out task, responseMode);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<RedisRespReader.RespValue> ExecuteAsync(
        ReadOnlyMemory<byte> header,
        ReadOnlyMemory<byte> payload,
        bool appendCrlf,
        bool poolBulk,
        CancellationToken ct,
        byte[]? headerBuffer = null,
        ReadOnlyMemory<byte>[]? payloads = null,
        int payloadCount = 0,
        ReadOnlyMemory<byte>[]? payloadArrayBuffer = null,
        RedisResponseMode responseMode = RedisResponseMode.Default)
        => ExecuteAsync(
            header,
            payload,
            appendCrlf,
            appendCrlfPerPayload: true,
            poolBulk,
            ct,
            headerBuffer,
            payloads,
            payloadCount,
            payloadArrayBuffer,
            responseMode);

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<RedisRespReader.RespValue> ExecuteAsync(
        ReadOnlyMemory<byte> header,
        ReadOnlyMemory<byte> payload,
        bool appendCrlf,
        bool appendCrlfPerPayload,
        bool poolBulk,
        CancellationToken ct,
        byte[]? headerBuffer = null,
        ReadOnlyMemory<byte>[]? payloads = null,
        int payloadCount = 0,
        ReadOnlyMemory<byte>[]? payloadArrayBuffer = null,
        RedisResponseMode responseMode = RedisResponseMode.Default)
    {
        if (CanUseCommandOnlyPath(payload, payloadCount, appendCrlf, headerBuffer, payloadArrayBuffer))
            return ExecuteAsync(header, poolBulk, ct, responseMode);

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        if (TryAcquireInFlightWithSpin(ct))
            return EnqueueAfterSlot(
                header,
                payload,
                payloads,
                payloadCount,
                payloadArrayBuffer,
                appendCrlf,
                appendCrlfPerPayload,
                poolBulk,
                headerBuffer,
                ct,
                responseMode);

        return EnqueueAfterSlotAsync(
            header,
            payload,
            payloads,
            payloadCount,
            payloadArrayBuffer,
            appendCrlf,
            appendCrlfPerPayload,
            poolBulk,
            headerBuffer,
            ct,
            responseMode);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryAcquireInFlightWithSpin(CancellationToken ct)
    {
        if (_inFlight.Wait(0, ct))
            return true;

        var spinner = new SpinWait();
        for (var i = 0; i < InFlightSpinAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (_inFlight.Wait(0, ct))
                return true;
            spinner.SpinOnce();
        }

        return false;
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryExecuteAsync(
        ReadOnlyMemory<byte> header,
        ReadOnlyMemory<byte> payload,
        bool appendCrlf,
        bool poolBulk,
        CancellationToken ct,
        out ValueTask<RedisRespReader.RespValue> task,
        byte[]? headerBuffer = null,
        ReadOnlyMemory<byte>[]? payloads = null,
        int payloadCount = 0,
        ReadOnlyMemory<byte>[]? payloadArrayBuffer = null,
        RedisResponseMode responseMode = RedisResponseMode.Default)
        => TryExecuteAsync(
            header,
            payload,
            appendCrlf,
            appendCrlfPerPayload: true,
            poolBulk,
            ct,
            out task,
            headerBuffer,
            payloads,
            payloadCount,
            payloadArrayBuffer,
            responseMode);

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryExecuteAsync(
        ReadOnlyMemory<byte> header,
        ReadOnlyMemory<byte> payload,
        bool appendCrlf,
        bool appendCrlfPerPayload,
        bool poolBulk,
        CancellationToken ct,
        out ValueTask<RedisRespReader.RespValue> task,
        byte[]? headerBuffer = null,
        ReadOnlyMemory<byte>[]? payloads = null,
        int payloadCount = 0,
        ReadOnlyMemory<byte>[]? payloadArrayBuffer = null,
        RedisResponseMode responseMode = RedisResponseMode.Default)
    {
        if (CanUseCommandOnlyPath(payload, payloadCount, appendCrlf, headerBuffer, payloadArrayBuffer))
            return TryExecuteAsync(header, poolBulk, ct, out task, responseMode);

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        if (!_inFlight.Wait(0))
        {
            task = default;
            return false;
        }

        return TryEnqueueAfterSlot(
            header,
            payload,
            payloads,
            payloadCount,
            payloadArrayBuffer,
            appendCrlf,
            appendCrlfPerPayload,
            poolBulk,
            headerBuffer,
            ct,
            out task,
            responseMode);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CanUseCommandOnlyPath(
        ReadOnlyMemory<byte> payload,
        int payloadCount,
        bool appendCrlf,
        byte[]? headerBuffer,
        ReadOnlyMemory<byte>[]? payloadArrayBuffer)
        => payload.IsEmpty &&
           payloadCount <= 0 &&
           !appendCrlf &&
           headerBuffer is null &&
           payloadArrayBuffer is null;

    private ValueTask<RedisRespReader.RespValue> EnqueueAfterSlot(
        ReadOnlyMemory<byte> command,
        bool poolBulk,
        CancellationToken ct,
        RedisResponseMode responseMode)
    {
        var op = RentOperation();
        var operationClass = poolBulk
            ? OperationClass.Bulk
            : ClassifyOperation(command, ReadOnlyMemory<byte>.Empty, null, payloadCount: 0);
        RecordLaneOperationStarted();
        op.Start(poolBulk, ct, holdsSlot: true, sequenceId: 0, operationClass, responseMode);

        var req = new PendingRequest(command, op);
        if (_writes.TryEnqueue(in req))
        {
            MarkRequestBuffersOwnedByMux(in req);
            return op.ValueTask;
        }

        return EnqueueWithQueueWaitAsync(req, op, ct);
    }

    private bool TryEnqueueAfterSlot(
        ReadOnlyMemory<byte> command,
        bool poolBulk,
        CancellationToken ct,
        out ValueTask<RedisRespReader.RespValue> task,
        RedisResponseMode responseMode)
    {
        var op = RentOperation();
        var operationClass = poolBulk
            ? OperationClass.Bulk
            : ClassifyOperation(command, ReadOnlyMemory<byte>.Empty, null, payloadCount: 0);
        RecordLaneOperationStarted();
        op.Start(poolBulk, ct, holdsSlot: true, sequenceId: 0, operationClass, responseMode);

        var req = new PendingRequest(command, op);
        if (_writes.TryEnqueue(in req))
        {
            MarkRequestBuffersOwnedByMux(in req);
            task = op.ValueTask;
            return true;
        }

        op.AbortEnqueueFailure(valueTaskWillBeObserved: false);
        task = default;
        return false;
    }

    private ValueTask<RedisRespReader.RespValue> EnqueueAfterSlot(
        ReadOnlyMemory<byte> header,
        ReadOnlyMemory<byte> payload,
        ReadOnlyMemory<byte>[]? payloads,
        int payloadCount,
        ReadOnlyMemory<byte>[]? payloadArrayBuffer,
        bool appendCrlf,
        bool appendCrlfPerPayload,
        bool poolBulk,
        byte[]? headerBuffer,
        CancellationToken ct,
        RedisResponseMode responseMode)
    {
        var op = RentOperation();
        var operationClass = poolBulk
            ? OperationClass.Bulk
            : ClassifyOperation(header, payload, payloads, payloadCount);
        RecordLaneOperationStarted();
        op.Start(poolBulk, ct, holdsSlot: true, sequenceId: 0, operationClass, responseMode);

        var req = new PendingRequest(header, op, payload, payloads, payloadCount, appendCrlf, appendCrlfPerPayload, headerBuffer, payloadArrayBuffer);
        if (_writes.TryEnqueue(in req))
        {
            MarkRequestBuffersOwnedByMux(in req);
            return op.ValueTask;
        }

        return EnqueueWithQueueWaitAsync(req, op, ct);
    }

    private bool TryEnqueueAfterSlot(
        ReadOnlyMemory<byte> header,
        ReadOnlyMemory<byte> payload,
        ReadOnlyMemory<byte>[]? payloads,
        int payloadCount,
        ReadOnlyMemory<byte>[]? payloadArrayBuffer,
        bool appendCrlf,
        bool appendCrlfPerPayload,
        bool poolBulk,
        byte[]? headerBuffer,
        CancellationToken ct,
        out ValueTask<RedisRespReader.RespValue> task,
        RedisResponseMode responseMode)
    {
        var op = RentOperation();
        var operationClass = poolBulk
            ? OperationClass.Bulk
            : ClassifyOperation(header, payload, payloads, payloadCount);
        RecordLaneOperationStarted();
        op.Start(poolBulk, ct, holdsSlot: true, sequenceId: 0, operationClass, responseMode);

        var req = new PendingRequest(header, op, payload, payloads, payloadCount, appendCrlf, appendCrlfPerPayload, headerBuffer, payloadArrayBuffer);
        if (_writes.TryEnqueue(in req))
        {
            MarkRequestBuffersOwnedByMux(in req);
            task = op.ValueTask;
            return true;
        }

        op.AbortEnqueueFailure(valueTaskWillBeObserved: false);
        task = default;
        return false;
    }

    private async ValueTask<RedisRespReader.RespValue> EnqueueAfterSlotAsync(
        ReadOnlyMemory<byte> command,
        bool poolBulk,
        CancellationToken ct,
        RedisResponseMode responseMode)
    {
        await _inFlight.WaitAsync(ct).ConfigureAwait(false);
        var op = RentOperation();
        var operationClass = poolBulk
            ? OperationClass.Bulk
            : ClassifyOperation(command, ReadOnlyMemory<byte>.Empty, null, payloadCount: 0);
        RecordLaneOperationStarted();
        op.Start(poolBulk, ct, holdsSlot: true, sequenceId: 0, operationClass, responseMode);

        var req = new PendingRequest(command, op);
        return await EnqueueWithQueueWaitAsync(req, op, ct).ConfigureAwait(false);
    }

    private async ValueTask<RedisRespReader.RespValue> EnqueueAfterSlotAsync(
        ReadOnlyMemory<byte> header,
        ReadOnlyMemory<byte> payload,
        ReadOnlyMemory<byte>[]? payloads,
        int payloadCount,
        ReadOnlyMemory<byte>[]? payloadArrayBuffer,
        bool appendCrlf,
        bool appendCrlfPerPayload,
        bool poolBulk,
        byte[]? headerBuffer,
        CancellationToken ct,
        RedisResponseMode responseMode)
    {
        await _inFlight.WaitAsync(ct).ConfigureAwait(false);
        var op = RentOperation();
        var operationClass = poolBulk
            ? OperationClass.Bulk
            : ClassifyOperation(header, payload, payloads, payloadCount);
        RecordLaneOperationStarted();
        op.Start(poolBulk, ct, holdsSlot: true, sequenceId: 0, operationClass, responseMode);

        var req = new PendingRequest(header, op, payload, payloads, payloadCount, appendCrlf, appendCrlfPerPayload, headerBuffer, payloadArrayBuffer);
        return await EnqueueWithQueueWaitAsync(req, op, ct).ConfigureAwait(false);
    }

    private async ValueTask<RedisRespReader.RespValue> EnqueueWithQueueWaitAsync(PendingRequest req, PendingOperation op, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            await _writes.EnqueueAsync(req, ct).ConfigureAwait(false);
            MarkRequestBuffersOwnedByMux(in req);
        }
        catch (Exception ex)
        {
            op.AbortUnqueued(ex);
            throw;
        }

        var elapsedTicks = Stopwatch.GetTimestamp() - start;
        UpdateQueueWaitEwma(elapsedTicks);
        if (IsRuntimeTelemetryEnabled())
        {
            var elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
            RedisMetrics.QueueWaitMs.Record(elapsedMs, _writeQueueWaitTags);
        }

        return await op.ValueTask.ConfigureAwait(false);
    }

    private OperationClass ClassifyOperation(
        ReadOnlyMemory<byte> command,
        ReadOnlyMemory<byte> payload,
        ReadOnlyMemory<byte>[]? payloads,
        int payloadCount)
    {
        var totalBytes = command.Length + payload.Length;
        if (payloadCount > 0 && payloads is not null)
        {
            for (var i = 0; i < payloadCount; i++)
            {
                totalBytes += payloads[i].Length;
                if (totalBytes > _bulkPayloadBytesThreshold)
                    return OperationClass.Bulk;
            }
        }

        if (totalBytes > _bulkPayloadBytesThreshold)
            return OperationClass.Bulk;

        return IsBulkMGetCommand(command)
            ? OperationClass.Bulk
            : OperationClass.Fast;
    }

    private bool IsBulkMGetCommand(ReadOnlyMemory<byte> command)
    {
        var span = command.Span;
        if (span.Length < 16 || span[0] != (byte)'*')
            return false;

        if (!TryReadRespInteger(span, 1, out var idx, out var arrayLen))
            return false;

        if (arrayLen <= _bulkMgetKeyThreshold + 1)
            return false;

        if (idx >= span.Length || span[idx] != (byte)'$')
            return false;

        if (!TryReadRespInteger(span, idx + 1, out idx, out var tokenLen))
            return false;

        if (tokenLen != 4 || idx + tokenLen + 2 > span.Length)
            return false;

        var token = span.Slice(idx, tokenLen);
        if (!IsMGetToken(token))
            return false;

        return span[idx + tokenLen] == (byte)'\r' && span[idx + tokenLen + 1] == (byte)'\n';
    }

    private static bool TryReadRespInteger(ReadOnlySpan<byte> span, int startIndex, out int nextIndex, out int value)
    {
        nextIndex = startIndex;
        value = 0;
        var sawDigit = false;

        for (var i = startIndex; i < span.Length; i++)
        {
            var b = span[i];
            if (b == (byte)'\r')
            {
                if (!sawDigit || i + 1 >= span.Length || span[i + 1] != (byte)'\n')
                    return false;

                nextIndex = i + 2;
                return true;
            }

            if (b is < (byte)'0' or > (byte)'9')
                return false;

            sawDigit = true;
            value = checked((value * 10) + (b - (byte)'0'));
        }

        return false;
    }

    private static bool IsMGetToken(ReadOnlySpan<byte> token)
        => token.Length == 4
            && ((token[0] | 0x20) == (byte)'m')
            && ((token[1] | 0x20) == (byte)'g')
            && ((token[2] | 0x20) == (byte)'e')
            && ((token[3] | 0x20) == (byte)'t');

    private static bool ShouldResetTransportForTimeout(PendingOperation operation)
    {
        _ = operation;
        // Timeout handling can interrupt a RESP frame mid-parse. Always recycle transport
        // to guarantee framing integrity for the next operation.
        return true;
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_conn is not null) return;
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_conn is not null) return;

            var created = await _factory.CreateAsync(ct).ConfigureAwait(false);
            _conn = created.Match(static c => c, static ex => throw ex);
            if (_useSocketReader)
            {
                _respSocketReader = new RedisRespSocketReaderState(
                    _conn.Socket,
                    useUnsafeFastPath: false,
                    maxBulkStringBytes: _maxBulkStringBytes,
                    maxArrayDepth: _maxArrayDepth,
                    onBytesRead: RecordLaneBytesReceived);
            }
            else
            {
                _respReader = new RedisRespReaderState(
                    _conn.Stream,
                    useUnsafeFastPath: false,
                    maxBulkStringBytes: _maxBulkStringBytes,
                    maxArrayDepth: _maxArrayDepth,
                    onBytesRead: RecordLaneBytesReceived);
            }

            Interlocked.Increment(ref _generation);

            // Successful connect/reset path clears transient unhealthy state.
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            Interlocked.Exchange(ref _recentTimeoutPenalty, 0);
            Interlocked.Exchange(ref _recentFailurePenalty, 0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _cts.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            Interlocked.Increment(ref _failureCount);
            Interlocked.Increment(ref _consecutiveFailures);
            RecordFailureSignal();
            throw;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task WriterLoopAsync()
    {
        const int DrainBatchLimit = 64;
        while (!_cts.IsCancellationRequested)
        {
            PendingRequest req = default;
            bool hasReq = false;
            var usedCoalescedPath = false;
            try
            {
                req = await _writes.DequeueAsync(_cts.Token).ConfigureAwait(false);
                hasReq = true;
                await EnsureConnectedAsync(_cts.Token).ConfigureAwait(false);
                var conn = _conn!;
                var generation = Volatile.Read(ref _generation);
                var drained = 0;
                do
                {
                    // Coalescing path supports all Redis command shapes, including payload operations.
                    // When coalescing is disabled, use the direct non-coalesced send path.
                    var useCoalescedPath = _coalesceWrites;

                    if (useCoalescedPath)
                    {
                        usedCoalescedPath = true;
                        await _coalescedWriteDispatcher.SendAsync(req, conn.Socket, generation, _cts.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        usedCoalescedPath = false;
                        await SendDirectAsync(req, conn, generation, _cts.Token).ConfigureAwait(false);
                        if (req.HeaderBuffer is not null)
                            ReturnHeaderBufferFromMux(req.HeaderBuffer);
                        if (req.PayloadArrayBuffer is not null)
                        {
                            // Preserve length; skip clearing to avoid extra work.
                            ReturnPayloadArrayFromMux(req.PayloadArrayBuffer);
                        }
                    }

                    hasReq = false;
                    req = default;
                }
                while (
                    drained++ < DrainBatchLimit &&
                    _writes.TryDequeue(out req) &&
                    (hasReq = true));
            }
            catch (OperationCanceledException oce) when (_cts.IsCancellationRequested)
            {
                // Complete any pending request that was dequeued before cancellation
                if (hasReq && req.Op is not null && !req.Op.IsCompleted)
                {
                    if (!usedCoalescedPath)
                        ReturnRequestBuffers(in req);
                    req.Op.AbortUnqueued(oce);
                }
                break;
            }
            catch (Exception ex)
            {
                if (hasReq && req.Op is not null && !req.Op.IsCompleted && !usedCoalescedPath)
                {
                    ReturnRequestBuffers(in req);
                    req.Op.AbortUnqueued(ex);
                }
                await FailTransportAsync(ex).ConfigureAwait(false);
            }
        }
    }

    private static void ReturnRequestBuffers(in PendingRequest req)
    {
        if (req.HeaderBuffer is not null)
            ReturnHeaderBufferFromMux(req.HeaderBuffer);
        if (req.PayloadArrayBuffer is not null)
            ReturnPayloadArrayFromMux(req.PayloadArrayBuffer);
    }

    private async Task ReaderLoopAsync()
    {
        const int DrainBatchLimit = 64;
        while (!_cts.IsCancellationRequested)
        {
            PendingOperation? next = null;
            try
            {
                next = await _pending.DequeueAsync(_cts.Token).ConfigureAwait(false);
                var drained = 0;
                do
                {
                    await ProcessPendingOperationAsync(next).ConfigureAwait(false);
                    next = null;
                }
                while (
                    drained++ < DrainBatchLimit &&
                    _pending.TryDequeue(out next) &&
                    next is not null);
            }
            catch (OperationCanceledException oce) when (_cts.IsCancellationRequested)
            {
                // Complete any pending operation that was dequeued before cancellation
                if (next is not null && !next.IsCompleted)
                    next.TrySetException(oce);
                break;
            }
            catch (Exception ex)
            {
                if (next is not null && !next.IsCompleted)
                {
                    next.TrySetException(ex);
                    next.MarkResponseProcessed();
                }
                await FailTransportAsync(ex).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessPendingOperationAsync(PendingOperation next)
    {
        await EnsureConnectedAsync(_cts.Token).ConfigureAwait(false);
        var readGeneration = Volatile.Read(ref _generation);
        var readResult = await _responseReaderLoop.ReadAsync(next.PoolBulk, next.ResponseMode).ConfigureAwait(false);
        if (readResult.HasResponse)
            RecordLaneResponseObserved(next, readResult.Response);

        if (next.IsCompleted)
        {
            if (readResult.HasResponse)
                RedisRespReader.ReturnBuffers(readResult.Response);
        }
        else if (readResult.Error is not null)
        {
            if (readResult.Error is TimeoutException timeout)
            {
                Interlocked.Increment(ref _responseTimeoutCount);
                RecordTimeoutSignal();
                if (ShouldResetTransportForTimeout(next))
                    await FailTransportAsync(timeout).ConfigureAwait(false);
            }
            if (readResult.HasResponse)
                RedisRespReader.ReturnBuffers(readResult.Response);
            next.TrySetException(readResult.Error);
        }
        else
        {
            if (!readResult.HasResponse)
                throw new InvalidOperationException("RESP reader returned null response without error.");

            var response = readResult.Response;
            var currentGeneration = Volatile.Read(ref _generation);
            if (next.Generation != currentGeneration || readGeneration != currentGeneration)
            {
                RedisRespReader.ReturnBuffers(response);
                next.TrySetException(
                    new IOException(
                        $"Stale transport generation response ignored. Operation generation {next.Generation}, current generation {currentGeneration}."));
                next.MarkResponseProcessed();
                return;
            }

            next.TrySetResult(response);
            RecordSuccessfulOperation();
        }

        next.MarkResponseProcessed();
    }

    private async Task SendDirectAsync(PendingRequest req, IRedisConnection conn, long generation, CancellationToken ct)
    {
        var sendHeader = await conn.SendAsync(req.Command, ct).ConfigureAwait(false);
        if (!sendHeader.IsSuccess)
            _ = sendHeader.IfFail(static ex => throw ex);
        RecordLaneBytesSent(req.Command.Length);
        if (!req.Payload.IsEmpty)
        {
            var sendPayload = await conn.SendAsync(req.Payload, ct).ConfigureAwait(false);
            if (!sendPayload.IsSuccess)
                _ = sendPayload.IfFail(static ex => throw ex);
            RecordLaneBytesSent(req.Payload.Length);
            if (req.AppendCrlf)
            {
                var sendCrlf = await conn.SendAsync(CrlfMemory, ct).ConfigureAwait(false);
                if (!sendCrlf.IsSuccess)
                    _ = sendCrlf.IfFail(static ex => throw ex);
                RecordLaneBytesSent(CrlfMemory.Length);
            }
        }
        else if (req.PayloadCount > 0 && req.Payloads is not null)
        {
            var payloads = req.Payloads;
            for (var i = 0; i < req.PayloadCount; i++)
            {
                var sendSegment = await conn.SendAsync(payloads[i], ct).ConfigureAwait(false);
                if (!sendSegment.IsSuccess)
                    _ = sendSegment.IfFail(static ex => throw ex);
                RecordLaneBytesSent(payloads[i].Length);
                if (req.AppendCrlfPerPayload)
                {
                    var sendLine = await conn.SendAsync(CrlfMemory, ct).ConfigureAwait(false);
                    if (!sendLine.IsSuccess)
                    _ = sendLine.IfFail(static ex => throw ex);
                    RecordLaneBytesSent(CrlfMemory.Length);
                }
            }
        }

        req.Op.AssignGeneration(generation);
        req.Op.AssignSequenceId(NextPendingResponseSequence());
        if (!_pending.TryEnqueue(req.Op))
            await _pending.EnqueueAsync(req.Op, ct).ConfigureAwait(false);
    }

    private async Task FailTransportAsync(Exception ex, bool countTransportReset = true)
    {
        Interlocked.Increment(ref _failureCount);
        Interlocked.Increment(ref _consecutiveFailures);
        RecordFailureSignal();
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _laneExpectedResponseSequence, 0);
        if (countTransportReset)
            Interlocked.Increment(ref _laneTransportResetCount);

        var reader = Interlocked.Exchange(ref _respReader, null);
        var socketReader = Interlocked.Exchange(ref _respSocketReader, null);
        var conn = Interlocked.Exchange(ref _conn, null);

        try
        {
            if (reader is not null)
                await reader.DisposeAsync().ConfigureAwait(false);
            if (socketReader is not null)
                await socketReader.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            if (conn is not null)
                await conn.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        while (_pending.TryDequeue(out var op))
        {
            if (op is null) continue;
            op.TrySetException(ex);
            op.MarkResponseProcessed();
        }

        while (_writes.TryDequeue(out var req))
        {
            if (req.HeaderBuffer is not null)
                ReturnHeaderBufferFromMux(req.HeaderBuffer);
            if (req.PayloadArrayBuffer is not null)
                ReturnPayloadArrayFromMux(req.PayloadArrayBuffer);
            if (req.Op is not null)
                req.Op.AbortUnqueued(ex);
        }
    }

    internal Task ResetTransportAsync(Exception ex)
        => FailTransportAsync(ex);

    private static bool IsFatalSocket(SocketError error) =>
        error == SocketError.OperationAborted ||
        error == SocketError.ConnectionReset ||
        error == SocketError.ConnectionAborted ||
        error == SocketError.TimedOut ||
        error == SocketError.NotConnected;

    /// <summary>
    /// Asynchronously releases resources used by the current instance.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (_runtimeTelemetryRegistered)
        {
            RedisTelemetry.UnregisterQueueDepthProvider(_connectionId);
            RedisTelemetry.UnregisterMuxLaneUsageProvider(_connectionId);
        }

        try { _cts.Cancel(); } catch { }

        var tasks = new[] { _writer, _reader }.Where(t => t != null).ToArray();
        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAny(
                    Task.WhenAll(tasks),
                    Task.Delay(TimeSpan.FromSeconds(5))
                ).ConfigureAwait(false);
            }
            catch { }
        }

        await FailTransportAsync(new ObjectDisposedException(nameof(RedisMultiplexedConnection)), countTransportReset: false).ConfigureAwait(false);

        if (_conn is not null)
        {
            try { await _conn.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        while (_operationPool.TryTake(out var pooledOp))
        {
            try
            {
                if (pooledOp is null)
                    continue;
                if (!pooledOp.IsCompleted)
                    pooledOp.TrySetException(new ObjectDisposedException(nameof(RedisMultiplexedConnection)));
                pooledOp.DisposeResources();
            }
            catch { }
        }

        try { _writes.Dispose(); } catch { }
        try { _pending.Dispose(); } catch { }

        try { _inFlight.Dispose(); } catch { }
        try { _coalescedWriteDispatcher.Dispose(); } catch { }

        try { _cts.Dispose(); } catch { }

        if (_respReader is not null)
        {
            try { await _respReader.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        if (_respSocketReader is not null)
        {
            try { await _respSocketReader.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsRuntimeTelemetryEnabled()
        => _shouldRecordRuntimeTelemetry?.Invoke() ?? true;

    private RedisTelemetry.QueueDepthSnapshot GetQueueDepthSnapshot()
        => IsRuntimeTelemetryEnabled()
            ? new RedisTelemetry.QueueDepthSnapshot(_writes.Count, _pending.Count, _writes.Capacity, _pending.Capacity)
            : new RedisTelemetry.QueueDepthSnapshot(0, 0, 0, 0);

    internal int ConnectionId => _connectionId;

    internal RedisTelemetry.MuxLaneUsageSnapshot CaptureMuxLaneUsageSnapshot()
        => GetMuxLaneUsageSnapshot();

    private RedisTelemetry.MuxLaneUsageSnapshot GetMuxLaneUsageTelemetrySnapshot()
        => IsRuntimeTelemetryEnabled()
            ? GetMuxLaneUsageSnapshot()
            : new RedisTelemetry.MuxLaneUsageSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private RedisTelemetry.MuxLaneUsageSnapshot GetMuxLaneUsageSnapshot()
    {
        var inFlight = 0;
        try
        {
            inFlight = _maxInFlight - _inFlight.CurrentCount;
        }
        catch (ObjectDisposedException)
        {
        }

        return new RedisTelemetry.MuxLaneUsageSnapshot(
            BytesSent: Interlocked.Read(ref _laneBytesSent),
            BytesReceived: Interlocked.Read(ref _laneBytesReceived),
            Operations: Interlocked.Read(ref _laneOperationsStarted),
            Failures: Interlocked.Read(ref _failureCount),
            Responses: Interlocked.Read(ref _laneResponsesObserved),
            OrphanedResponses: Interlocked.Read(ref _laneOrphanedResponses),
            ResponseSequenceMismatches: Interlocked.Read(ref _laneResponseSequenceMismatches),
            TransportResets: Interlocked.Read(ref _laneTransportResetCount),
            InFlight: inFlight,
            MaxInFlight: _maxInFlight);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordLaneBytesSent(int bytes)
    {
        if (bytes > 0)
            Interlocked.Add(ref _laneBytesSent, bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordLaneBytesReceived(int bytes)
    {
        if (bytes > 0)
            Interlocked.Add(ref _laneBytesReceived, bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long RecordLaneOperationStarted()
        => Interlocked.Increment(ref _laneOperationsStarted);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long NextPendingResponseSequence()
        => Interlocked.Increment(ref _lanePendingSequenceAssigned);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateQueueWaitEwma(long elapsedTicks)
    {
        if (elapsedTicks <= 0)
            return;

        var sampleMicros = (elapsedTicks * 1_000_000L) / Stopwatch.Frequency;
        if (sampleMicros < 0)
            sampleMicros = 0;
        else if (sampleMicros > 4_000_000)
            sampleMicros = 4_000_000;

        var sampleQ8 = sampleMicros << 8;
        while (true)
        {
            var current = Volatile.Read(ref _queueWaitEwmaMicrosQ8);
            var next = current == 0
                ? sampleQ8
                : current + ((sampleQ8 - current) >> 3); // alpha ~= 0.125

            if (Interlocked.CompareExchange(ref _queueWaitEwmaMicrosQ8, next, current) == current)
                return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordTimeoutSignal()
        => AddBounded(ref _recentTimeoutPenalty, amount: 64, max: 4096);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordFailureSignal()
        => AddBounded(ref _recentFailurePenalty, amount: 96, max: 4096);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordSuccessfulOperation()
    {
        DecayBounded(ref _recentTimeoutPenalty, amount: 8);
        DecayBounded(ref _recentFailurePenalty, amount: 4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddBounded(ref int target, int amount, int max)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            var remaining = max - current;
            if (remaining <= 0)
                return;

            var next = current + Math.Min(amount, remaining);
            if (Interlocked.CompareExchange(ref target, next, current) == current)
                return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecayBounded(ref int target, int amount)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (current <= 0)
                return;

            var next = current > amount ? current - amount : 0;
            if (Interlocked.CompareExchange(ref target, next, current) == current)
                return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordLaneResponseObserved(PendingOperation op, RedisRespReader.RespValue response)
    {
        _ = response;
        Interlocked.Increment(ref _laneResponsesObserved);
        var operationSequence = op.SequenceId;
        if (operationSequence > 0)
        {
            var expectedSequence = Volatile.Read(ref _laneExpectedResponseSequence);
            if (expectedSequence != 0 && operationSequence != expectedSequence)
                Interlocked.Increment(ref _laneResponseSequenceMismatches);

            Volatile.Write(ref _laneExpectedResponseSequence, operationSequence + 1);
        }

        if (op.IsCompleted)
            Interlocked.Increment(ref _laneOrphanedResponses);
    }

    private PendingOperation RentOperation()
        => _operationPool.Rent();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MarkRequestBuffersOwnedByMux(in PendingRequest request)
    {
        if (request.HeaderBuffer is not null)
            RedisMultiplexedBufferCaches.MarkHeaderBufferInFlight(request.HeaderBuffer);
        if (request.PayloadArrayBuffer is not null)
            RedisMultiplexedBufferCaches.MarkPayloadArrayInFlight(request.PayloadArrayBuffer);
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance method keeps the connection-facing API stable for command-executor call sites.")]
    internal byte[] RentHeaderBuffer(int minLength)
        => RedisMultiplexedBufferCaches.RentHeaderBuffer(minLength);

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance method keeps the connection-facing API stable for command-executor call sites.")]
    internal void ReturnHeaderBuffer(byte[] buffer)
        => RedisMultiplexedBufferCaches.ReturnHeaderBufferFromCaller(buffer);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReturnHeaderBufferFromMux(byte[]? buffer)
        => RedisMultiplexedBufferCaches.ReturnHeaderBufferFromMux(buffer);

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance method keeps the connection-facing API stable for command-executor call sites.")]
    internal ReadOnlyMemory<byte>[] RentPayloadArray(int minLength)
        => RedisMultiplexedBufferCaches.RentPayloadArray(minLength);

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance method keeps the connection-facing API stable for command-executor call sites.")]
    internal void ReturnPayloadArray(ReadOnlyMemory<byte>[]? payloads)
        => RedisMultiplexedBufferCaches.ReturnPayloadArrayFromCaller(payloads);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReturnPayloadArrayFromMux(ReadOnlyMemory<byte>[]? payloads)
        => RedisMultiplexedBufferCaches.ReturnPayloadArrayFromMux(payloads);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetLaneSelectionScore()
    {
        var inFlight = _maxInFlight - _inFlight.CurrentCount;
        return _writes.Count + (inFlight >> 4);
    }

    internal async ValueTask PrimeAsync(CancellationToken ct)
    {
        if (_cts.IsCancellationRequested)
            return;

        await EnsureConnectedAsync(ct).ConfigureAwait(false);
    }

    internal int WriteQueueDepth => _writes.Count;
    internal int InFlightCount => _maxInFlight - _inFlight.CurrentCount;
    internal int MaxInFlight => _maxInFlight;
    internal int QueueWaitEwmaMicros => (int)(Volatile.Read(ref _queueWaitEwmaMicrosQ8) >> 8);
    internal long ResponseTimeoutCount => Interlocked.Read(ref _responseTimeoutCount);
    internal long FailureCount => Interlocked.Read(ref _failureCount);
    internal int TimeoutPenalty => Volatile.Read(ref _recentTimeoutPenalty);
    internal int FailurePenalty => Volatile.Read(ref _recentFailurePenalty);
    internal int ConsecutiveFailureCount => Volatile.Read(ref _consecutiveFailures);
    internal bool IsHealthy => Volatile.Read(ref _consecutiveFailures) == 0;

    // Bounded MPSC -> single-consumer ring with semaphore-based coordination (no per-op allocations).
    // Bounded MPSC -> single-consumer ring with semaphore coordination (no per-op allocations on the hot path).
    private sealed class MpscRingQueue<T> : IDisposable
    {
        private readonly T[] _buffer;
        private readonly long[] _sequence;
        private readonly int _mask;
        private readonly MultiWaiterSignal _slotsAvailable = new();
        private readonly MultiWaiterSignal _itemsAvailable = new();
        private long _head;
        private long _tail;

        public MpscRingQueue(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
            if ((capacity & (capacity - 1)) != 0)
                throw new ArgumentException("Capacity must be a power of two.", nameof(capacity));

            _buffer = new T[capacity];
            _sequence = new long[capacity];
            for (var i = 0; i < capacity; i++)
                _sequence[i] = i;

            _mask = capacity - 1;
            _head = 0;
            _tail = 0;
        }

        public int Capacity => _buffer.Length;
        public int Count => (int)Math.Min((long)Capacity, Volatile.Read(ref _head) - Volatile.Read(ref _tail));
        public bool IsEmpty => Count == 0;
        public bool IsFull => Count == Capacity;

        /// <summary>
        /// Attempts to value.
        /// </summary>
        public bool TryEnqueue(in T item)
        {
            if (IsFull)
                return false;

            if (TryEnqueueCore(in item))
            {
                _itemsAvailable.Set();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Executes value.
        /// </summary>
        public ValueTask EnqueueAsync(T item, CancellationToken ct)
        {
            var spinner = new SpinWait();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsFull && TryEnqueueCore(in item))
                {
                    _itemsAvailable.Set();
                    return ValueTask.CompletedTask;
                }

                if (spinner.Count >= 10)
                    break;

                spinner.SpinOnce();
            }

            return EnqueueAsyncSlow(item, ct);
        }

        private async ValueTask EnqueueAsyncSlow(T item, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsFull && TryEnqueueCore(in item))
                {
                    _itemsAvailable.Set();
                    return;
                }

                var observedVersion = _slotsAvailable.Version;
                if (Count < Capacity)
                    continue;

                await _slotsAvailable.WaitAsync(observedVersion, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Test helper: EnqueueAsync without the spin-wait loop, directly using the async path.
        /// Used to test synchronous completion handling in unit tests.
        /// </summary>
        private ValueTask EnqueueAsyncNoSpinForTests(T item, CancellationToken ct)
            => EnqueueAsyncSlow(item, ct);

        private bool TryEnqueueCore(in T item)
        {
            while (true)
            {
                var pos = Volatile.Read(ref _head);
                var tail = Volatile.Read(ref _tail);
                if (pos - tail >= _buffer.Length)
                    return false;

                var idx = (int)(pos & _mask);
                var seq = Volatile.Read(ref _sequence[idx]);
                var dif = seq - pos;
                if (dif == 0)
                {
                    if (Interlocked.CompareExchange(ref _head, pos + 1, pos) == pos)
                    {
                        _buffer[idx] = item;
                        Volatile.Write(ref _sequence[idx], pos + 1);
                        return true;
                    }
                    continue;
                }

                if (dif < 0)
                    return false;
            }
        }

        /// <summary>
        /// Attempts to value.
        /// </summary>
        public bool TryDequeue(out T? item)
        {
            return TryDequeueCore(out item);
        }

        /// <summary>
        /// Executes value.
        /// </summary>
        public ValueTask<T> DequeueAsync(CancellationToken ct)
        {
            var spinner = new SpinWait();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (TryDequeueCore(out var item))
                    return new ValueTask<T>(item!);

                if (spinner.Count >= 10)
                    break;

                spinner.SpinOnce();
            }

            return DequeueAsyncSlow(ct);
        }

        private async ValueTask<T> DequeueAsyncSlow(CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (TryDequeueCore(out var item))
                    return item!;

                var observedVersion = _itemsAvailable.Version;
                if (!IsEmpty)
                    continue;

                await _itemsAvailable.WaitAsync(observedVersion, ct).ConfigureAwait(false);
            }
        }

        private bool TryDequeueCore(out T? item)
        {
            var pos = Volatile.Read(ref _tail);
            while (true)
            {
                var idx = (int)(pos & _mask);
                var seq = Volatile.Read(ref _sequence[idx]);
                var dif = seq - (pos + 1);
                if (dif == 0)
                {
                    // Try to claim this slot using CAS for thread-safety
                    if (Interlocked.CompareExchange(ref _tail, pos + 1, pos) == pos)
                    {
                        item = _buffer[idx];
                        _buffer[idx] = default!;
                        Volatile.Write(ref _sequence[idx], pos + _buffer.Length);
                        _slotsAvailable.Set();
                        return true;
                    }
                    pos = Volatile.Read(ref _tail);
                    continue;
                }

                if (dif < 0)
                {
                    item = default;
                    return false;
                }

                Thread.SpinWait(1);
            }
        }

        /// <summary>
        /// Attempts to value.
        /// </summary>
        public bool TryPeek(out T? item)
        {
            var pos = Volatile.Read(ref _tail);
            var idx = (int)(pos & _mask);
            var seq = Volatile.Read(ref _sequence[idx]);
            if (seq - (pos + 1) != 0)
            {
                item = default;
                return false;
            }
            item = _buffer[idx];
            return true;
        }

        /// <summary>
        /// Executes value.
        /// </summary>
        public void Clear()
        {
            while (TryDequeue(out _)) ;
        }

        /// <summary>
        /// Releases resources used by the current instance.
        /// </summary>
        public void Dispose()
        {
            _slotsAvailable.Dispose();
            _itemsAvailable.Dispose();
        }
    }

    // SPSC ring for the pending response path (single writer: transport thread, single reader: parser thread).
    private sealed class SpscRingQueue<T> : IDisposable
    {
        private readonly T[] _buffer;
        private readonly int _mask;
        private readonly SingleWaiterSignal _slotsAvailable = new();
        private readonly SingleWaiterSignal _itemsAvailable = new();
        private int _head;
        private int _tail;

        public SpscRingQueue(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
            if ((capacity & (capacity - 1)) != 0)
                throw new ArgumentException("Capacity must be a power of two.", nameof(capacity));

            _buffer = new T[capacity];
            _mask = capacity - 1;
            _head = 0;
            _tail = 0;
        }

        public int Capacity => _buffer.Length;
        public int Count
        {
            get
            {
                var head = Volatile.Read(ref _head);
                var tail = Volatile.Read(ref _tail);
                var count = (long)head - tail;
                if (count < 0) count = 0;
                if (count > Capacity) count = Capacity;
                return (int)count;
            }
        }

        /// <summary>
        /// Attempts to value.
        /// </summary>
        public bool TryEnqueue(T item)
        {
            if (Count >= Capacity)
                return false;

            var idx = _head & _mask;
            _buffer[idx] = item;
            Volatile.Write(ref _head, _head + 1);
            _itemsAvailable.Set();
            return true;
        }

        /// <summary>
        /// Executes value.
        /// </summary>
        public ValueTask EnqueueAsync(T item, CancellationToken ct)
        {
            var spinner = new SpinWait();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (TryEnqueue(item))
                {
                    return ValueTask.CompletedTask;
                }

                if (spinner.Count >= 10)
                    break;

                spinner.SpinOnce();
            }

            return EnqueueAsyncSlow(item, ct);
        }

        private async ValueTask EnqueueAsyncSlow(T item, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (TryEnqueue(item))
                    return;

                await _slotsAvailable.WaitAsync(ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Test helper: EnqueueAsync without the spin-wait loop, directly using the async path.
        /// Used to test synchronous completion handling in unit tests.
        /// </summary>
        private ValueTask EnqueueAsyncNoSpinForTests(T item, CancellationToken ct)
            => EnqueueAsyncSlow(item, ct);

        /// <summary>
        /// Attempts to value.
        /// </summary>
        public bool TryDequeue(out T? item)
        {
            if (Count <= 0)
            {
                item = default;
                return false;
            }

            var idx = _tail & _mask;
            item = _buffer[idx];
            _buffer[idx] = default!;
            Volatile.Write(ref _tail, _tail + 1);
            _slotsAvailable.Set();
            return true;
        }

        /// <summary>
        /// Executes value.
        /// </summary>
        public ValueTask<T> DequeueAsync(CancellationToken ct)
        {
            var spinner = new SpinWait();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (TryDequeue(out var item))
                    return new ValueTask<T>(item!);

                if (spinner.Count >= 10)
                    break;

                spinner.SpinOnce();
            }

            return DequeueAsyncSlow(ct);
        }

        private async ValueTask<T> DequeueAsyncSlow(CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (TryDequeue(out var item))
                    return item!;

                await _itemsAvailable.WaitAsync(ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Executes value.
        /// </summary>
        public void Clear()
        {
            while (TryDequeue(out _)) ;
        }

        /// <summary>
        /// Releases resources used by the current instance.
        /// </summary>
        public void Dispose()
        {
            _slotsAvailable.Dispose();
            _itemsAvailable.Dispose();
        }
    }

    private sealed class SingleWaiterSignal : IValueTaskSource<bool>, IDisposable
    {
        private ManualResetValueTaskSourceCore<bool> _core;
        private CancellationTokenRegistration _ctr;
        private CancellationToken _waitingToken;
        private int _state; // 0 = idle, 1 = waiting, 2 = signaled

        public SingleWaiterSignal()
        {
            _core = new ManualResetValueTaskSourceCore<bool>
            {
                RunContinuationsAsynchronously = true
            };
        }

        public void Set()
        {
            while (true)
            {
                var state = Volatile.Read(ref _state);
                switch (state)
                {
                    case 0:
                        if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
                            return;
                        break;
                    case 1:
                        if (Interlocked.CompareExchange(ref _state, 0, 1) == 1)
                        {
                            _ctr.Dispose();
                            _core.SetResult(true);
                            return;
                        }
                        break;
                    case 2:
                        return;
                }
            }
        }

        public ValueTask<bool> WaitAsync(CancellationToken ct)
        {
            if (TryConsumeSignal())
                return ValueTask.FromResult(true);

            ct.ThrowIfCancellationRequested();

            _core.Reset();
            _waitingToken = ct;
            _ctr = ct.CanBeCanceled
                ? ct.Register(static state => ((SingleWaiterSignal)state!).CancelWait(), this)
                : default;

            var priorState = Interlocked.CompareExchange(ref _state, 1, 0);
            if (priorState == 2)
            {
                Volatile.Write(ref _state, 0);
                _ctr.Dispose();
                return ValueTask.FromResult(true);
            }

            if (priorState != 0)
                throw new InvalidOperationException("Concurrent waiters are not supported.");

            return new ValueTask<bool>(this, _core.Version);
        }

        public void Dispose()
        {
            _ctr.Dispose();
        }

        private bool TryConsumeSignal()
            => Interlocked.CompareExchange(ref _state, 0, 2) == 2;

        private void CancelWait()
        {
            if (Interlocked.CompareExchange(ref _state, 0, 1) != 1)
                return;

            _core.SetException(new OperationCanceledException(_waitingToken));
        }

        bool IValueTaskSource<bool>.GetResult(short token)
        {
            _ctr.Dispose();
            return _core.GetResult(token);
        }

        ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token)
            => _core.GetStatus(token);

        void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);
    }

    private sealed class MultiWaiterSignal : IDisposable
    {
        private volatile TaskCompletionSource<bool>? _waiters;
        private long _version;

        public long Version => Volatile.Read(ref _version);

        public void Set()
        {
            Interlocked.Increment(ref _version);
            var waiters = Interlocked.Exchange(ref _waiters, null);
            waiters?.TrySetResult(true);
        }

        public ValueTask WaitAsync(long observedVersion, CancellationToken ct)
        {
            if (Volatile.Read(ref _version) != observedVersion)
                return ValueTask.CompletedTask;

            while (true)
            {
                var current = _waiters;
                if (Volatile.Read(ref _version) != observedVersion)
                    return ValueTask.CompletedTask;

                if (current is null)
                {
                    var created = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var prior = Interlocked.CompareExchange(ref _waiters, created, null);
                    current = prior ?? created;
                    if (prior is not null)
                        continue;
                }

                if (Volatile.Read(ref _version) != observedVersion)
                    return ValueTask.CompletedTask;

                return new ValueTask(current.Task.WaitAsync(ct));
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _waiters, null)?.TrySetCanceled();
        }
    }
}
