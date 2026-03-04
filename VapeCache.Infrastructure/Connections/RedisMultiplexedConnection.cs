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
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisMultiplexedConnection : IAsyncDisposable
{
    private static int _nextConnectionId;
    private readonly IRedisConnectionFactory _factory;
    private readonly int _maxInFlight;
    private readonly bool _coalesceWrites;
    private readonly bool _useSocketReader;
    private readonly bool _useDedicatedLaneWorkers;
    private readonly int _maxBulkStringBytes;
    private readonly int _maxArrayDepth;
    private readonly TimeSpan _responseTimeout;
    private readonly int _connectionId;

    private readonly MpscRingQueue<PendingRequest> _writes;
    private readonly SpscRingQueue<PendingOperation> _pending;

    private readonly SemaphoreSlim _inFlight;
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
        Action<long>? recordLatencyStopwatchTicks = null,
        Func<bool>? shouldRecordLatency = null)
    {
        _factory = factory;
        _maxInFlight = Math.Max(1, maxInFlight);
        _coalesceWrites = coalesceWrites;
        _useSocketReader = enableSocketRespReader;
        _useDedicatedLaneWorkers = useDedicatedLaneWorkers;
        _maxBulkStringBytes = maxBulkStringBytes;
        _maxArrayDepth = maxArrayDepth;
        _responseTimeout = responseTimeout <= TimeSpan.Zero || responseTimeout == Timeout.InfiniteTimeSpan
            ? TimeSpan.Zero
            : responseTimeout;
        _inFlight = new SemaphoreSlim(_maxInFlight, _maxInFlight);
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
            ReturnHeaderBuffer,
            ReturnPayloadArray,
            static (req, ex) => req.Op.AbortUnqueued(ex),
            RecordLaneBytesSent);

        _connectionId = Interlocked.Increment(ref _nextConnectionId);
        RedisTelemetry.RegisterQueueDepthProvider(_connectionId, GetQueueDepthSnapshot);
        RedisTelemetry.RegisterMuxLaneUsageProvider(_connectionId, GetMuxLaneUsageSnapshot);

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
        ExecuteAsync(command, poolBulk: false, ct);

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<RedisRespReader.RespValue> ExecuteAsync(
        ReadOnlyMemory<byte> command,
        bool poolBulk,
        CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(RedisMultiplexedConnection));

        if (_inFlight.Wait(0))
            return EnqueueAfterSlot(command, poolBulk, ct);

        return EnqueueAfterSlotAsync(command, poolBulk, ct);
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryExecuteAsync(
        ReadOnlyMemory<byte> command,
        bool poolBulk,
        CancellationToken ct,
        out ValueTask<RedisRespReader.RespValue> task)
    {
        if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(RedisMultiplexedConnection));

        if (!_inFlight.Wait(0))
        {
            task = default;
            return false;
        }

        return TryEnqueueAfterSlot(command, poolBulk, ct, out task);
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
        ReadOnlyMemory<byte>[]? payloadArrayBuffer = null)
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
            payloadArrayBuffer);

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
        ReadOnlyMemory<byte>[]? payloadArrayBuffer = null)
    {
        if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(RedisMultiplexedConnection));

        if (_inFlight.Wait(0))
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
                ct);

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
            ct);
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
        ReadOnlyMemory<byte>[]? payloadArrayBuffer = null)
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
            payloadArrayBuffer);

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
        ReadOnlyMemory<byte>[]? payloadArrayBuffer = null)
    {
        if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(RedisMultiplexedConnection));

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
            out task);
    }

    private ValueTask<RedisRespReader.RespValue> EnqueueAfterSlot(
        ReadOnlyMemory<byte> command,
        bool poolBulk,
        CancellationToken ct)
    {
        var op = RentOperation();
        RecordLaneOperationStarted();
        op.Start(poolBulk, ct, holdsSlot: true, sequenceId: 0);

        var req = new PendingRequest(command, op);
        if (_writes.TryEnqueue(req))
            return op.ValueTask;

        return EnqueueWithQueueWaitAsync(req, op, ct);
    }

    private bool TryEnqueueAfterSlot(
        ReadOnlyMemory<byte> command,
        bool poolBulk,
        CancellationToken ct,
        out ValueTask<RedisRespReader.RespValue> task)
    {
        var op = RentOperation();
        RecordLaneOperationStarted();
        op.Start(poolBulk, ct, holdsSlot: true, sequenceId: 0);

        var req = new PendingRequest(command, op);
        if (_writes.TryEnqueue(req))
        {
            task = op.ValueTask;
            return true;
        }

        op.AbortEnqueueFailure();
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
        CancellationToken ct)
    {
        var op = RentOperation();
        RecordLaneOperationStarted();
        op.Start(poolBulk, ct, holdsSlot: true, sequenceId: 0);

        var req = new PendingRequest(header, op, payload, payloads, payloadCount, appendCrlf, appendCrlfPerPayload, headerBuffer, payloadArrayBuffer);
        if (_writes.TryEnqueue(req))
            return op.ValueTask;

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
        out ValueTask<RedisRespReader.RespValue> task)
    {
        var op = RentOperation();
        RecordLaneOperationStarted();
        op.Start(poolBulk, ct, holdsSlot: true, sequenceId: 0);

        var req = new PendingRequest(header, op, payload, payloads, payloadCount, appendCrlf, appendCrlfPerPayload, headerBuffer, payloadArrayBuffer);
        if (_writes.TryEnqueue(req))
        {
            task = op.ValueTask;
            return true;
        }

        op.AbortEnqueueFailure();
        task = default;
        return false;
    }

    private async ValueTask<RedisRespReader.RespValue> EnqueueAfterSlotAsync(
        ReadOnlyMemory<byte> command,
        bool poolBulk,
        CancellationToken ct)
    {
        await _inFlight.WaitAsync(ct).ConfigureAwait(false);
        var op = RentOperation();
        RecordLaneOperationStarted();
        op.Start(poolBulk, ct, holdsSlot: true, sequenceId: 0);

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
        CancellationToken ct)
    {
        await _inFlight.WaitAsync(ct).ConfigureAwait(false);
        var op = RentOperation();
        RecordLaneOperationStarted();
        op.Start(poolBulk, ct, holdsSlot: true, sequenceId: 0);

        var req = new PendingRequest(header, op, payload, payloads, payloadCount, appendCrlf, appendCrlfPerPayload, headerBuffer, payloadArrayBuffer);
        return await EnqueueWithQueueWaitAsync(req, op, ct).ConfigureAwait(false);
    }

    private async ValueTask<RedisRespReader.RespValue> EnqueueWithQueueWaitAsync(PendingRequest req, PendingOperation op, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            await _writes.EnqueueAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            op.AbortUnqueued(ex);
            throw;
        }
        var elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        RedisTelemetry.QueueWaitMs.Record(elapsedMs, new TagList { { "queue", "writes" }, { "connection.id", _connectionId } });
        return await op.ValueTask.ConfigureAwait(false);
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
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _cts.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            Interlocked.Increment(ref _failureCount);
            Interlocked.Increment(ref _consecutiveFailures);
            throw;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task WriterLoopAsync()
    {
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
                    await SendDirectAsync(req, conn, generation, _cts.Token).ConfigureAwait(false);
                    if (req.HeaderBuffer is not null)
                        ReturnHeaderBuffer(req.HeaderBuffer);
                    if (req.PayloadArrayBuffer is not null)
                    {
                        // Preserve length; skip clearing to avoid extra work.
                        ReturnPayloadArray(req.PayloadArrayBuffer);
                    }
                }
            }
            catch (OperationCanceledException oce) when (_cts.IsCancellationRequested)
            {
                // Complete any pending request that was dequeued before cancellation
                if (hasReq && req.Op is not null && !req.Op.IsCompleted)
                {
                    if (!usedCoalescedPath)
                        ReturnRequestBuffers(req);
                    req.Op.AbortUnqueued(oce);
                }
                break;
            }
            catch (Exception ex)
            {
                if (hasReq && req.Op is not null && !req.Op.IsCompleted && !usedCoalescedPath)
                {
                    ReturnRequestBuffers(req);
                    req.Op.AbortUnqueued(ex);
                }
                await FailTransportAsync(ex).ConfigureAwait(false);
            }
        }
    }

    private void ReturnRequestBuffers(PendingRequest req)
    {
        if (req.HeaderBuffer is not null)
            ReturnHeaderBuffer(req.HeaderBuffer);
        if (req.PayloadArrayBuffer is not null)
            ReturnPayloadArray(req.PayloadArrayBuffer);
    }

    private async Task ReaderLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            PendingOperation? next = null;
            try
            {
                next = await _pending.DequeueAsync(_cts.Token).ConfigureAwait(false);
                await EnsureConnectedAsync(_cts.Token).ConfigureAwait(false);
                var readGeneration = Volatile.Read(ref _generation);
                var (resp, ex) = await _responseReaderLoop.ReadAsync(next.PoolBulk).ConfigureAwait(false);
                var currentGeneration = Volatile.Read(ref _generation);

                if (next.Generation != currentGeneration || readGeneration != currentGeneration)
                {
                    if (resp is not null)
                        RedisRespReader.ReturnBuffers(resp);

                    next.TrySetException(
                        new IOException(
                            $"Stale transport generation response ignored. Operation generation {next.Generation}, current generation {currentGeneration}."));
                    next.MarkResponseProcessed();
                    continue;
                }

                if (resp is not null)
                    RecordLaneResponseObserved(next, resp);

                if (next.IsCompleted)
                {
                    if (resp is not null)
                        RedisRespReader.ReturnBuffers(resp);
                }
                else if (ex is not null)
                {
                    if (ex is TimeoutException)
                        Interlocked.Increment(ref _responseTimeoutCount);
                    if (resp is not null)
                        RedisRespReader.ReturnBuffers(resp);
                    next.TrySetException(ex);
                }
                else
                {
                    if (resp is null)
                        throw new InvalidOperationException("RESP reader returned null response without error.");
                    next.TrySetResult(resp);
                }

                next.MarkResponseProcessed();
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
                await FailTransportAsync(ex).ConfigureAwait(false);
            }
        }
    }

    private async Task SendDirectAsync(PendingRequest req, IRedisConnection conn, long generation, CancellationToken ct)
    {
        var sendHeader = await conn.SendAsync(req.Command, ct).ConfigureAwait(false);
        if (!sendHeader.IsSuccess)
            sendHeader.IfFail(static ex => throw ex);
        RecordLaneBytesSent(req.Command.Length);
        if (!req.Payload.IsEmpty)
        {
            var sendPayload = await conn.SendAsync(req.Payload, ct).ConfigureAwait(false);
            if (!sendPayload.IsSuccess)
                sendPayload.IfFail(static ex => throw ex);
            RecordLaneBytesSent(req.Payload.Length);
            if (req.AppendCrlf)
            {
                var sendCrlf = await conn.SendAsync(CrlfMemory, ct).ConfigureAwait(false);
                if (!sendCrlf.IsSuccess)
                    sendCrlf.IfFail(static ex => throw ex);
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
                    sendSegment.IfFail(static ex => throw ex);
                RecordLaneBytesSent(payloads[i].Length);
                if (req.AppendCrlfPerPayload)
                {
                    var sendLine = await conn.SendAsync(CrlfMemory, ct).ConfigureAwait(false);
                    if (!sendLine.IsSuccess)
                    sendLine.IfFail(static ex => throw ex);
                    RecordLaneBytesSent(CrlfMemory.Length);
                }
            }
        }

        req.Op.AssignGeneration(generation);
        req.Op.AssignSequenceId(NextPendingResponseSequence());
        await _pending.EnqueueAsync(req.Op, ct).ConfigureAwait(false);
    }

    private async Task FailTransportAsync(Exception ex, bool countTransportReset = true)
    {
        Interlocked.Increment(ref _failureCount);
        Interlocked.Increment(ref _consecutiveFailures);
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
                ReturnHeaderBuffer(req.HeaderBuffer);
            if (req.PayloadArrayBuffer is not null)
                ReturnPayloadArray(req.PayloadArrayBuffer);
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
        RedisTelemetry.UnregisterQueueDepthProvider(_connectionId);
        RedisTelemetry.UnregisterMuxLaneUsageProvider(_connectionId);

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

    private RedisTelemetry.QueueDepthSnapshot GetQueueDepthSnapshot()
        => new(_writes.Count, _pending.Count, _writes.Capacity, _pending.Capacity);

    internal int ConnectionId => _connectionId;

    internal RedisTelemetry.MuxLaneUsageSnapshot CaptureMuxLaneUsageSnapshot()
        => GetMuxLaneUsageSnapshot();

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

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance method keeps the connection-facing API stable for command-executor call sites.")]
    internal byte[] RentHeaderBuffer(int minLength)
        => RedisMultiplexedBufferCaches.RentHeaderBuffer(minLength);

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance method keeps the connection-facing API stable for command-executor call sites.")]
    internal void ReturnHeaderBuffer(byte[] buffer)
        => RedisMultiplexedBufferCaches.ReturnHeaderBuffer(buffer);

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance method keeps the connection-facing API stable for command-executor call sites.")]
    internal ReadOnlyMemory<byte>[] RentPayloadArray(int minLength)
        => RedisMultiplexedBufferCaches.RentPayloadArray(minLength);

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance method keeps the connection-facing API stable for command-executor call sites.")]
    internal void ReturnPayloadArray(ReadOnlyMemory<byte>[]? payloads)
        => RedisMultiplexedBufferCaches.ReturnPayloadArray(payloads);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetLaneSelectionScore()
    {
        var inFlight = _maxInFlight - _inFlight.CurrentCount;
        return _writes.Count + (inFlight >> 4);
    }

    internal int WriteQueueDepth => _writes.Count;
    internal int InFlightCount => _maxInFlight - _inFlight.CurrentCount;
    internal int MaxInFlight => _maxInFlight;
    internal long ResponseTimeoutCount => Interlocked.Read(ref _responseTimeoutCount);
    internal long FailureCount => Interlocked.Read(ref _failureCount);
    internal bool IsHealthy => Volatile.Read(ref _consecutiveFailures) == 0;

    // Bounded MPSC -> single-consumer ring with semaphore-based coordination (no per-op allocations).
    // Bounded MPSC -> single-consumer ring with semaphore coordination (no per-op allocations on the hot path).
    private sealed class MpscRingQueue<T> : IDisposable
    {
        private readonly T[] _buffer;
        private readonly long[] _sequence;
        private readonly int _mask;
        private readonly SemaphoreSlim _slots;
        private readonly SemaphoreSlim _items;
        private long _head;
        private long _tail;

        public MpscRingQueue(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if ((capacity & (capacity - 1)) != 0)
                throw new ArgumentException("Capacity must be a power of two.", nameof(capacity));

            _buffer = new T[capacity];
            _sequence = new long[capacity];
            for (var i = 0; i < capacity; i++)
                _sequence[i] = i;

            _mask = capacity - 1;
            _head = 0;
            _tail = 0;
            _slots = new SemaphoreSlim(capacity, capacity);
            _items = new SemaphoreSlim(0, capacity);
        }

        public int Capacity => _buffer.Length;
        public int Count => (int)Math.Min((long)Capacity, Volatile.Read(ref _head) - Volatile.Read(ref _tail));
        public bool IsEmpty => Count == 0;
        public bool IsFull => Count == Capacity;

        /// <summary>
        /// Attempts to value.
        /// </summary>
        public bool TryEnqueue(T item)
        {
            if (!_slots.Wait(0))
                return false;

            if (TryEnqueueCore(item))
            {
                _items.Release();
                return true;
            }

            _slots.Release();
            return false;
        }

        /// <summary>
        /// Executes value.
        /// </summary>
        public ValueTask EnqueueAsync(T item, CancellationToken ct)
        {
            var spinner = new SpinWait();
            while (spinner.Count < 10)
            {
                ct.ThrowIfCancellationRequested();
                if (_slots.Wait(0))
                {
                    EnqueueAfterSlot(item);
                    return ValueTask.CompletedTask;
                }
                spinner.SpinOnce();
            }

            var wait = _slots.WaitAsync(ct);
            if (wait.IsCompletedSuccessfully)
            {
                EnqueueAfterSlot(item);
                return ValueTask.CompletedTask;
            }
            return EnqueueAsyncSlow(item, wait);
        }

        private async ValueTask EnqueueAsyncSlow(T item, Task slotWait)
        {
            var slotAcquired = false;
            try
            {
                await slotWait.ConfigureAwait(false);
                slotAcquired = true;
                EnqueueAfterSlot(item);
            }
            catch
            {
                if (slotAcquired)
                    _slots.Release();
                throw;
            }
        }

        private void EnqueueAfterSlot(T item)
        {
            if (!TryEnqueueCore(item))
                throw new InvalidOperationException("Ring enqueue failed unexpectedly.");
            _items.Release();
        }

        /// <summary>
        /// Test helper: EnqueueAsync without the spin-wait loop, directly using the async path.
        /// Used to test synchronous completion handling in unit tests.
        /// </summary>
        private ValueTask EnqueueAsyncNoSpinForTests(T item, CancellationToken ct)
        {
            var wait = _slots.WaitAsync(ct);
            if (wait.IsCompletedSuccessfully)
            {
                EnqueueAfterSlot(item);
                return ValueTask.CompletedTask;
            }
            return EnqueueAsyncSlow(item, wait);
        }

        private bool TryEnqueueCore(T item)
        {
            while (true)
            {
                var pos = Volatile.Read(ref _head);
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
            if (!_items.Wait(0))
            {
                item = default;
                return false;
            }

            item = DequeueAfterWait();
            return true;
        }

        /// <summary>
        /// Executes value.
        /// </summary>
        public ValueTask<T> DequeueAsync(CancellationToken ct)
        {
            var spinner = new SpinWait();
            while (spinner.Count < 10)
            {
                ct.ThrowIfCancellationRequested();
                if (_items.Wait(0))
                    return new ValueTask<T>(DequeueAfterWait());
                spinner.SpinOnce();
            }

            var wait = _items.WaitAsync(ct);
            return wait.IsCompletedSuccessfully
                ? new ValueTask<T>(DequeueAfterWait())
                : DequeueAsyncSlow(wait);
        }

        private async ValueTask<T> DequeueAsyncSlow(Task wait)
        {
            await wait.ConfigureAwait(false);
            return DequeueAfterWait();
        }

        private T DequeueAfterWait()
        {
            var spinner = new SpinWait();
            while (true)
            {
                if (TryDequeueCore(out var item, allowBailout: false))
                {
                    _slots.Release();
                    return item!;
                }

                spinner.SpinOnce();
            }
        }

        private bool TryDequeueCore(out T? item, bool allowBailout = true)
        {
            var pos = Volatile.Read(ref _tail);
            var spins = 0;
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
                        return true;
                    }
                    // Another thread claimed it, reload position and retry
                    pos = Volatile.Read(ref _tail);
                    continue;
                }

                if (dif < 0)
                {
                    item = default;
                    return false;
                }

                // In non-guaranteed paths we allow a bounded bailout; after a successful _items.Wait
                // the caller must continue until a slot is observable.
                spins++;
                if (allowBailout && spins > 100)
                {
                    // Too many spins, likely a race condition - bail out
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
            if (!_items.Wait(0))
            {
                item = default;
                return false;
            }

            var pos = Volatile.Read(ref _tail);
            var idx = (int)(pos & _mask);
            var seq = Volatile.Read(ref _sequence[idx]);
            var dif = seq - (pos + 1);
            if (dif == 0)
            {
                item = _buffer[idx];
                _items.Release();
                return true;
            }

            _items.Release();
            item = default;
            return false;
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
            _slots?.Dispose();
            _items?.Dispose();
        }
    }

    // SPSC ring for the pending response path (single writer: transport thread, single reader: parser thread).
    private sealed class SpscRingQueue<T> : IDisposable
    {
        private readonly T[] _buffer;
        private readonly int _mask;
        private readonly SemaphoreSlim _slots;
        private readonly SemaphoreSlim _items;
        private int _head;
        private int _tail;

        public SpscRingQueue(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if ((capacity & (capacity - 1)) != 0)
                throw new ArgumentException("Capacity must be a power of two.", nameof(capacity));

            _buffer = new T[capacity];
            _mask = capacity - 1;
            _head = 0;
            _tail = 0;
            _slots = new SemaphoreSlim(capacity, capacity);
            _items = new SemaphoreSlim(0, capacity);
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
            if (!_slots.Wait(0))
                return false;

            var idx = _head & _mask;
            _buffer[idx] = item;
            _head++;
            _items.Release();
            return true;
        }

        /// <summary>
        /// Executes value.
        /// </summary>
        public ValueTask EnqueueAsync(T item, CancellationToken ct)
        {
            var spinner = new SpinWait();
            while (spinner.Count < 10)
            {
                ct.ThrowIfCancellationRequested();
                if (_slots.Wait(0))
                {
                    EnqueueAfterSlot(item);
                    return ValueTask.CompletedTask;
                }
                spinner.SpinOnce();
            }

            var wait = _slots.WaitAsync(ct);
            if (wait.IsCompletedSuccessfully)
            {
                EnqueueAfterSlot(item);
                return ValueTask.CompletedTask;
            }
            return EnqueueAsyncSlow(item, wait);
        }

        private async ValueTask EnqueueAsyncSlow(T item, Task wait)
        {
            var slotAcquired = false;
            try
            {
                await wait.ConfigureAwait(false);
                slotAcquired = true;
                EnqueueAfterSlot(item);
            }
            catch
            {
                if (slotAcquired)
                    _slots.Release();
                throw;
            }
        }

        private void EnqueueAfterSlot(T item)
        {
            var idx = _head & _mask;
            _buffer[idx] = item;
            _head++;
            _items.Release();
        }

        /// <summary>
        /// Test helper: EnqueueAsync without the spin-wait loop, directly using the async path.
        /// Used to test synchronous completion handling in unit tests.
        /// </summary>
        private ValueTask EnqueueAsyncNoSpinForTests(T item, CancellationToken ct)
        {
            var wait = _slots.WaitAsync(ct);
            if (wait.IsCompletedSuccessfully)
            {
                EnqueueAfterSlot(item);
                return ValueTask.CompletedTask;
            }
            return EnqueueAsyncSlow(item, wait);
        }

        /// <summary>
        /// Attempts to value.
        /// </summary>
        public bool TryDequeue(out T? item)
        {
            if (!_items.Wait(0))
            {
                item = default;
                return false;
            }

            var idx = _tail & _mask;
            item = _buffer[idx];
            _buffer[idx] = default!;
            _tail++;
            _slots.Release();
            return true;
        }

        /// <summary>
        /// Executes value.
        /// </summary>
        public ValueTask<T> DequeueAsync(CancellationToken ct)
        {
            var spinner = new SpinWait();
            while (spinner.Count < 10)
            {
                ct.ThrowIfCancellationRequested();
                if (_items.Wait(0))
                    return new ValueTask<T>(DequeueAfterWait());
                spinner.SpinOnce();
            }

            var wait = _items.WaitAsync(ct);
            return wait.IsCompletedSuccessfully
                ? new ValueTask<T>(DequeueAfterWait())
                : DequeueAsyncSlow(wait);
        }

        private async ValueTask<T> DequeueAsyncSlow(Task wait)
        {
            await wait.ConfigureAwait(false);
            return DequeueAfterWait();
        }

        private T DequeueAfterWait()
        {
            var idx = _tail & _mask;
            var item = _buffer[idx];
            _buffer[idx] = default!;
            _tail++;
            _slots.Release();
            return item!;
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
            _slots?.Dispose();
            _items?.Dispose();
        }
    }
}
