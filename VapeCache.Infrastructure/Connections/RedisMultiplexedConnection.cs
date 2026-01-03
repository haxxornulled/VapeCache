using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisMultiplexedConnection : IAsyncDisposable
{
    private static int _nextConnectionId;
    // Toggle to disable the experimental socket-based RESP reader; benchmarks run with stream reader for stability.
    private const bool EnableSocketRespReader = false;
    private readonly IRedisConnectionFactory _factory;
    private readonly int _maxInFlight;
    private readonly bool _coalesceWrites;
    private readonly bool _useSocketReader;
    private readonly int _maxBulkStringBytes;
    private readonly int _maxArrayDepth;
    private readonly TimeSpan _responseTimeout;
    private readonly int _connectionId;

    private readonly MpscRingQueue<PendingRequest> _writes;
    private readonly SpscRingQueue<PendingOperation> _pending;

    private readonly SemaphoreSlim _inFlight;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly ConcurrentBag<PendingOperation> _opPool = new();

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writer;
    private readonly Task _reader;
    private static readonly ReadOnlyMemory<byte> CrlfMemory = "\r\n"u8.ToArray();

    // Per-thread caches (fast path - no locks)
    [ThreadStatic] private static byte[]? _tlsHeaderCache;
    [ThreadStatic] private static byte[]? _tlsSmallHeaderCache;
    [ThreadStatic] private static ReadOnlyMemory<byte>[]? _tlsPayloadArrayCache;
    [ThreadStatic] private static ReadOnlyMemory<byte>[]? _tlsSmallPayloadArrayCache;

    // Shared pools for cross-thread returns
    private static readonly ConcurrentBag<byte[]> _sharedHeaderCache = new();
    private static readonly ConcurrentBag<byte[]> _sharedSmallHeaderCache = new();
    private static readonly ConcurrentBag<ReadOnlyMemory<byte>[]> _sharedPayloadArrayCache = new();
    private static readonly ConcurrentBag<ReadOnlyMemory<byte>[]> _sharedSmallPayloadArrayCache = new();

    private const int MaxSharedCacheSize = 64;
    private readonly Queue<CoalescedPendingRequest> _coalesceQueue = new(16);
    private readonly List<PendingRequest> _coalesceDrained = new(8);
    private readonly List<CoalescedPendingRequest> _coalesceCaptured = new(8);
    private readonly Coalescer _coalescer;
    private readonly CoalescedWriteBatch _coalesceBatch = new();
    private readonly ReadOnlyMemory<byte>[][] _coalesceSegmentsPool8 = new ReadOnlyMemory<byte>[8][];
    private int _coalesceSegmentsPool8Count;
    private readonly ArrayPool<ReadOnlyMemory<byte>> _coalesceSegmentArrayPool = ArrayPool<ReadOnlyMemory<byte>>.Shared;

    private IRedisConnection? _conn;
    private RedisRespReaderState? _respReader;
    private RedisRespSocketReaderState? _respSocketReader;
    private int _disposed;


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
        int maxBulkStringBytes = 16 * 1024 * 1024,
        int maxArrayDepth = 64,
        TimeSpan responseTimeout = default)
    {
        _factory = factory;
        _maxInFlight = Math.Max(1, maxInFlight);
        _coalesceWrites = coalesceWrites;
        _useSocketReader = EnableSocketRespReader && coalesceWrites;
        _maxBulkStringBytes = maxBulkStringBytes;
        _maxArrayDepth = maxArrayDepth;
        _responseTimeout = responseTimeout <= TimeSpan.Zero || responseTimeout == Timeout.InfiniteTimeSpan
            ? TimeSpan.Zero
            : responseTimeout;
        _inFlight = new SemaphoreSlim(_maxInFlight, _maxInFlight);

        var capacity = RoundUpToPowerOfTwo(Math.Max(128, _maxInFlight * 4));
        _writes = new MpscRingQueue<PendingRequest>(capacity);
        _pending = new SpscRingQueue<PendingOperation>(capacity);
        _coalescer = new Coalescer(_coalesceQueue);

        _connectionId = Interlocked.Increment(ref _nextConnectionId);
        RedisTelemetry.RegisterQueueDepthProvider(_connectionId, GetQueueDepthSnapshot);

        _writer = Task.Run(WriterLoopAsync);
        _reader = Task.Run(ReaderLoopAsync);
    }

    public ValueTask<RedisRespReader.RespValue> ExecuteAsync(ReadOnlyMemory<byte> command, CancellationToken ct) =>
        ExecuteAsync(command, poolBulk: false, ct);

    public ValueTask<RedisRespReader.RespValue> ExecuteAsync(ReadOnlyMemory<byte> command, bool poolBulk, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(RedisMultiplexedConnection));

        if (_inFlight.Wait(0))
            return EnqueueAfterSlot(command, poolBulk, ct);

        return EnqueueAfterSlotAsync(command, poolBulk, ct);
    }

    public bool TryExecuteAsync(ReadOnlyMemory<byte> command, bool poolBulk, CancellationToken ct, out ValueTask<RedisRespReader.RespValue> task)
    {
        if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(RedisMultiplexedConnection));

        if (!_inFlight.Wait(0))
        {
            task = default;
            return false;
        }

        return TryEnqueueAfterSlot(command, poolBulk, ct, out task);
    }

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
    {
        if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(RedisMultiplexedConnection));

        if (_inFlight.Wait(0))
            return EnqueueAfterSlot(header, payload, payloads, payloadCount, payloadArrayBuffer, appendCrlf, poolBulk, headerBuffer, ct);

        return EnqueueAfterSlotAsync(header, payload, payloads, payloadCount, payloadArrayBuffer, appendCrlf, poolBulk, headerBuffer, ct);
    }

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
    {
        if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(RedisMultiplexedConnection));

        if (!_inFlight.Wait(0))
        {
            task = default;
            return false;
        }

        return TryEnqueueAfterSlot(header, payload, payloads, payloadCount, payloadArrayBuffer, appendCrlf, poolBulk, headerBuffer, ct, out task);
    }

    private ValueTask<RedisRespReader.RespValue> EnqueueAfterSlot(ReadOnlyMemory<byte> command, bool poolBulk, CancellationToken ct)
    {
        var op = RentOperation();
        op.Start(poolBulk, ct, holdsSlot: true);

        var req = new PendingRequest(command, op);
        if (_writes.TryEnqueue(req))
            return op.ValueTask;

        return EnqueueWithQueueWaitAsync(req, op, ct);
    }

    private bool TryEnqueueAfterSlot(ReadOnlyMemory<byte> command, bool poolBulk, CancellationToken ct, out ValueTask<RedisRespReader.RespValue> task)
    {
        var op = RentOperation();
        op.Start(poolBulk, ct, holdsSlot: true);

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

    private ValueTask<RedisRespReader.RespValue> EnqueueAfterSlot(ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> payload, ReadOnlyMemory<byte>[]? payloads, int payloadCount, ReadOnlyMemory<byte>[]? payloadArrayBuffer, bool appendCrlf, bool poolBulk, byte[]? headerBuffer, CancellationToken ct)
    {
        var op = RentOperation();
        op.Start(poolBulk, ct, holdsSlot: true);

        var req = new PendingRequest(header, op, payload, payloads, payloadCount, appendCrlf, headerBuffer, payloadArrayBuffer);
        if (_writes.TryEnqueue(req))
            return op.ValueTask;

        return EnqueueWithQueueWaitAsync(req, op, ct);
    }

    private bool TryEnqueueAfterSlot(ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> payload, ReadOnlyMemory<byte>[]? payloads, int payloadCount, ReadOnlyMemory<byte>[]? payloadArrayBuffer, bool appendCrlf, bool poolBulk, byte[]? headerBuffer, CancellationToken ct, out ValueTask<RedisRespReader.RespValue> task)
    {
        var op = RentOperation();
        op.Start(poolBulk, ct, holdsSlot: true);

        var req = new PendingRequest(header, op, payload, payloads, payloadCount, appendCrlf, headerBuffer, payloadArrayBuffer);
        if (_writes.TryEnqueue(req))
        {
            task = op.ValueTask;
            return true;
        }

        op.AbortEnqueueFailure();
        task = default;
        return false;
    }

    private async ValueTask<RedisRespReader.RespValue> EnqueueAfterSlotAsync(ReadOnlyMemory<byte> command, bool poolBulk, CancellationToken ct)
    {
        await _inFlight.WaitAsync(ct).ConfigureAwait(false);
        var op = RentOperation();
        op.Start(poolBulk, ct, holdsSlot: true);

        var req = new PendingRequest(command, op);
        return await EnqueueWithQueueWaitAsync(req, op, ct).ConfigureAwait(false);
    }

    private async ValueTask<RedisRespReader.RespValue> EnqueueAfterSlotAsync(ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> payload, ReadOnlyMemory<byte>[]? payloads, int payloadCount, ReadOnlyMemory<byte>[]? payloadArrayBuffer, bool appendCrlf, bool poolBulk, byte[]? headerBuffer, CancellationToken ct)
    {
        await _inFlight.WaitAsync(ct).ConfigureAwait(false);
        var op = RentOperation();
        op.Start(poolBulk, ct, holdsSlot: true);

        var req = new PendingRequest(header, op, payload, payloads, payloadCount, appendCrlf, headerBuffer, payloadArrayBuffer);
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
                    maxArrayDepth: _maxArrayDepth);
            }
            else
            {
                _respReader = new RedisRespReaderState(
                    _conn.Stream,
                    useUnsafeFastPath: false,
                    maxBulkStringBytes: _maxBulkStringBytes,
                    maxArrayDepth: _maxArrayDepth);
            }
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
            try
            {
                req = await _writes.DequeueAsync(_cts.Token).ConfigureAwait(false);
                hasReq = true;
                await EnsureConnectedAsync(_cts.Token).ConfigureAwait(false);
                var conn = _conn!;

                // Coalescing now works for ALL Redis command types, including payload operations.
                // Fixed scratch buffer reuse bug that was causing protocol corruption for multi-segment commands.
                var shouldCoalesce = _coalesceWrites;

                if (shouldCoalesce)
                {
                    await SendCoalescedAsync(req, conn.Socket, _cts.Token).ConfigureAwait(false);
                }
                else
                {
                    await SendLegacyAsync(req, conn, _cts.Token).ConfigureAwait(false);
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
                    req.Op.AbortUnqueued(oce);
                break;
            }
            catch (Exception ex)
            {
                await FailTransportAsync(ex).ConfigureAwait(false);
            }
        }
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
                RedisRespReader.RespValue? resp = null;
                Exception? ex = null;
                CancellationTokenSource? timeoutCts = null;
                var readToken = _cts.Token;
                if (_responseTimeout > TimeSpan.Zero)
                {
                    timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    timeoutCts.CancelAfter(_responseTimeout);
                    readToken = timeoutCts.Token;
                }
                try
                {
                    if (_useSocketReader)
                    {
                        var reader = _respSocketReader ?? throw new InvalidOperationException("RESP socket reader missing after connection established.");
                        try
                        {
                            resp = await reader.ReadAsync(next.PoolBulk, readToken).ConfigureAwait(false);
                            if (resp.Kind == RedisRespReader.RespKind.Error)
                                ex = new InvalidOperationException(resp.Text ?? "Redis error");
                        }
                        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (OperationCanceledException oce)
                        {
                            ex = new TimeoutException($"Redis response timed out after {_responseTimeout}.", oce);
                            await FailTransportAsync(ex).ConfigureAwait(false);
                        }
                        catch (SocketException se) when (IsFatalSocket(se.SocketErrorCode))
                        {
                            ex = se;
                            await FailTransportAsync(se).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            ex = e;
                        }
                    }
                    else
                    {
                        var reader = _respReader ?? throw new InvalidOperationException("RESP reader missing after connection established.");
                        try
                        {
                            resp = await reader.ReadAsync(next.PoolBulk, readToken).ConfigureAwait(false);
                            if (resp.Kind == RedisRespReader.RespKind.Error)
                                ex = new InvalidOperationException(resp.Text ?? "Redis error");
                        }
                        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (OperationCanceledException oce)
                        {
                            ex = new TimeoutException($"Redis response timed out after {_responseTimeout}.", oce);
                            await FailTransportAsync(ex).ConfigureAwait(false);
                        }
                        catch (IOException ioe) when (ioe.InnerException is SocketException se && IsFatalSocket(se.SocketErrorCode))
                        {
                            ex = ioe;
                            await FailTransportAsync(ioe).ConfigureAwait(false);
                        }
                        catch (SocketException se) when (IsFatalSocket(se.SocketErrorCode))
                        {
                            ex = se;
                            await FailTransportAsync(se).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            ex = e;
                        }
                    }
                }
                finally
                {
                    timeoutCts?.Dispose();
                }

                if (next.IsCompleted)
                {
                    if (ex is null && resp is not null)
                        RedisRespReader.ReturnBuffers(resp);
                }
                else if (ex is not null)
                {
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

    private async Task SendLegacyAsync(PendingRequest req, IRedisConnection conn, CancellationToken ct)
    {
        await _pending.EnqueueAsync(req.Op, ct).ConfigureAwait(false);
        var sendHeader = await conn.SendAsync(req.Command, ct).ConfigureAwait(false);
        if (!sendHeader.IsSuccess)
            sendHeader.IfFail(static ex => throw ex);
        if (!req.Payload.IsEmpty)
        {
            var sendPayload = await conn.SendAsync(req.Payload, ct).ConfigureAwait(false);
            if (!sendPayload.IsSuccess)
                sendPayload.IfFail(static ex => throw ex);
            if (req.AppendCrlf)
            {
                var sendCrlf = await conn.SendAsync(CrlfMemory, ct).ConfigureAwait(false);
                if (!sendCrlf.IsSuccess)
                    sendCrlf.IfFail(static ex => throw ex);
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
                var sendLine = await conn.SendAsync(CrlfMemory, ct).ConfigureAwait(false);
                if (!sendLine.IsSuccess)
                    sendLine.IfFail(static ex => throw ex);
            }
        }
    }

    private async Task SendCoalescedAsync(PendingRequest first, Socket socket, CancellationToken ct)
    {
        _coalesceQueue.Clear();
        _coalesceDrained.Clear();
        _coalesceCaptured.Clear();
        _coalesceDrained.Add(first);

        var firstCoalesced = ToCoalesced(first);
        _coalesceQueue.Enqueue(firstCoalesced);
        _coalesceCaptured.Add(firstCoalesced);

        // Opportunistically drain any immediately available requests to pack into this batch.
        while (_writes.TryDequeue(out var nextReq))
        {
            _coalesceDrained.Add(nextReq);
            var c = ToCoalesced(nextReq);
            _coalesceQueue.Enqueue(c);
            _coalesceCaptured.Add(c);
            if (_coalesceQueue.Count >= 8) break; // small bound to avoid starving reader
        }

        var coalescer = _coalescer;
        var batch = _coalesceBatch;
        while (coalescer.TryBuildBatch(batch))
        {
            ct.ThrowIfCancellationRequested();
            var totalBytes = 0;
            for (var i = 0; i < batch.SegmentsToWrite.Count; i++)
                totalBytes += batch.SegmentsToWrite[i].Length;

            if (totalBytes > 0)
            {
                var rented = ArrayPool<byte>.Shared.Rent(totalBytes);
                try
                {
                    var offset = 0;
                    for (var i = 0; i < batch.SegmentsToWrite.Count; i++)
                    {
                        var segment = batch.SegmentsToWrite[i];
                        segment.CopyTo(rented.AsMemory(offset, segment.Length));
                        offset += segment.Length;
                    }

                    await SendAllAsync(socket, rented.AsMemory(0, totalBytes), ct).ConfigureAwait(false);
                    RedisTelemetry.BytesSent.Add(totalBytes);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
            batch.RecycleAfterSend();
        }

        // NOW enqueue all operations to _pending AFTER socket send completed successfully
        for (var i = 0; i < _coalesceDrained.Count; i++)
        {
            await _pending.EnqueueAsync(_coalesceDrained[i].Op, ct).ConfigureAwait(false);
        }

        // Return buffers for drained requests.
        for (var i = 0; i < _coalesceDrained.Count; i++)
        {
            var r = _coalesceDrained[i];
            if (r.HeaderBuffer is not null)
                ReturnHeaderBuffer(r.HeaderBuffer);
            if (r.PayloadArrayBuffer is not null)
                ReturnPayloadArray(r.PayloadArrayBuffer);
        }
        for (var i = 0; i < _coalesceCaptured.Count; i++)
        {
            ReturnCoalesceSegments(_coalesceCaptured[i].Segments);
        }
    }

    private CoalescedPendingRequest ToCoalesced(PendingRequest req)
    {
        int count = 1;
        if (!req.Payload.IsEmpty)
        {
            count += req.AppendCrlf ? 2 : 1;
        }
        else if (req.PayloadCount > 0)
        {
            count += req.PayloadCount * 2;
        }

        var segments = RentCoalesceSegments(count);
        var idx = 0;
        segments[idx++] = req.Command;

        if (!req.Payload.IsEmpty)
        {
            segments[idx++] = req.Payload;
            if (req.AppendCrlf)
                segments[idx++] = CrlfMemory;
        }
        else if (req.PayloadCount > 0 && req.Payloads is not null)
        {
            for (var i = 0; i < req.PayloadCount; i++)
            {
                segments[idx++] = req.Payloads[i];
                segments[idx++] = CrlfMemory;
            }
        }

        return new CoalescedPendingRequest(segments, count, payloadOwner: null);
    }

    private ReadOnlyMemory<byte>[] RentCoalesceSegments(int minLength)
    {
        if (minLength <= 8 && _coalesceSegmentsPool8Count > 0)
        {
            var idx = --_coalesceSegmentsPool8Count;
            var cached = _coalesceSegmentsPool8[idx];
            _coalesceSegmentsPool8[idx] = Array.Empty<ReadOnlyMemory<byte>>();
            if (cached.Length >= minLength)
                return cached;
            _coalesceSegmentArrayPool.Return(cached, clearArray: true);
        }

        var size = minLength <= 8 ? 8 : minLength;
        return _coalesceSegmentArrayPool.Rent(size);
    }

    private void ReturnCoalesceSegments(ReadOnlyMemory<byte>[] segments)
    {
        if (segments.Length == 8 && _coalesceSegmentsPool8Count < _coalesceSegmentsPool8.Length)
        {
            _coalesceSegmentsPool8[_coalesceSegmentsPool8Count++] = segments;
            return;
        }

        _coalesceSegmentArrayPool.Return(segments, clearArray: true);
    }

    private static async ValueTask SendAllAsync(Socket socket, ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        while (!buffer.IsEmpty)
        {
            var sent = await socket.SendAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
            if (sent <= 0)
                throw new IOException("Socket send returned 0.");
            buffer = buffer.Slice(sent);
        }
    }


    private async Task FailTransportAsync(Exception ex)
    {
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

    private static bool IsFatalSocket(SocketError error) =>
        error == SocketError.OperationAborted ||
        error == SocketError.ConnectionReset ||
        error == SocketError.ConnectionAborted ||
        error == SocketError.TimedOut ||
        error == SocketError.NotConnected;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        RedisTelemetry.UnregisterQueueDepthProvider(_connectionId);

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

        await FailTransportAsync(new ObjectDisposedException(nameof(RedisMultiplexedConnection))).ConfigureAwait(false);

        if (_conn is not null)
        {
            try { await _conn.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        while (_opPool.TryTake(out var pooledOp))
        {
            try
            {
                if (!pooledOp.IsCompleted)
                    pooledOp.TrySetException(new ObjectDisposedException(nameof(RedisMultiplexedConnection)));
                pooledOp.DisposeResources();
            }
            catch { }
        }

        try { _writes.Dispose(); } catch { }
        try { _pending.Dispose(); } catch { }

        try { _inFlight.Dispose(); } catch { }
        try { _coalesceBatch.Dispose(); } catch { }

        try { _cts.Dispose(); } catch { }

        while (_coalesceSegmentsPool8Count > 0)
        {
            var arr = _coalesceSegmentsPool8[--_coalesceSegmentsPool8Count];
            if (arr.Length > 0)
                _coalesceSegmentArrayPool.Return(arr, clearArray: true);
        }

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

    private PendingOperation RentOperation()
    {
        if (_opPool.TryTake(out var op))
        {
            op.Reset(this);
            return op;
        }
        return new PendingOperation(this);
    }

    private void ReturnOperation(PendingOperation op) => _opPool.Add(op);

    private readonly record struct PendingRequest(
        ReadOnlyMemory<byte> Command,
        PendingOperation Op,
        ReadOnlyMemory<byte> Payload,
        ReadOnlyMemory<byte>[]? Payloads,
        int PayloadCount,
        bool AppendCrlf,
        byte[]? HeaderBuffer,
        ReadOnlyMemory<byte>[]? PayloadArrayBuffer)
    {
        public PendingRequest(ReadOnlyMemory<byte> command, PendingOperation op)
            : this(command, op, ReadOnlyMemory<byte>.Empty, null, 0, false, null, null) { }
    }

    internal byte[] RentHeaderBuffer(int minLength)
    {
        if (minLength <= 512)
        {
            // FAST PATH: Try thread-local cache first (lock-free)
            if (_tlsSmallHeaderCache is { Length: >= 512 } buf)
            {
                _tlsSmallHeaderCache = null;
                return buf;
            }

            if (_sharedSmallHeaderCache.TryTake(out var poolBuf) && poolBuf.Length >= minLength)
            {
                return poolBuf;
            }

            return new byte[512];
        }

        // Large buffer path (same pattern)
        if (_tlsHeaderCache is { Length: >= 2048 } largeBuf)
        {
            _tlsHeaderCache = null;
            return largeBuf;
        }

        if (_sharedHeaderCache.TryTake(out var largePoolBuf) && largePoolBuf.Length >= minLength)
        {
            return largePoolBuf;
        }

        return new byte[Math.Max(2048, minLength)];
    }

    internal void ReturnHeaderBuffer(byte[] buffer)
    {
        if (buffer is null) return;

        if (buffer.Length <= 512)
        {
            // FAST PATH: Try to cache in thread-local slot (lock-free)
            if (_tlsSmallHeaderCache is null)
            {
                _tlsSmallHeaderCache = buffer;
                return;
            }

            if (_sharedSmallHeaderCache.Count < MaxSharedCacheSize)
            {
                _sharedSmallHeaderCache.Add(buffer);
            }
            return;
        }

        // Large buffer path (same pattern)
        if (_tlsHeaderCache is null)
        {
            _tlsHeaderCache = buffer;
            return;
        }

        if (_sharedHeaderCache.Count < MaxSharedCacheSize)
        {
            _sharedHeaderCache.Add(buffer);
        }
    }

    internal ReadOnlyMemory<byte>[] RentPayloadArray(int minLength)
    {
        if (minLength <= 16)
        {
            // FAST PATH: Try thread-local cache first (lock-free)
            if (_tlsSmallPayloadArrayCache is { Length: >= 16 } arr)
            {
                _tlsSmallPayloadArrayCache = null;
                return arr;
            }

            // FALLBACK: Try shared pool
            if (_sharedSmallPayloadArrayCache.TryTake(out var poolArr) && poolArr.Length >= minLength)
            {
                return poolArr;
            }

            // SLOW PATH: Allocate new array
            return new ReadOnlyMemory<byte>[16];
        }

        // Large array path (same pattern)
        if (_tlsPayloadArrayCache is { Length: >= 64 } largeArr)
        {
            _tlsPayloadArrayCache = null;
            return largeArr;
        }

        if (_sharedPayloadArrayCache.TryTake(out var largePoolArr) && largePoolArr.Length >= minLength)
        {
            return largePoolArr;
        }

        return new ReadOnlyMemory<byte>[Math.Max(64, minLength)];
    }

    internal void ReturnPayloadArray(ReadOnlyMemory<byte>[]? payloads)
    {
        if (payloads is null) return;

        if (payloads.Length <= 16)
        {
            // FAST PATH: Try to cache in thread-local slot (lock-free)
            if (_tlsSmallPayloadArrayCache is null)
            {
                Array.Clear(payloads, 0, payloads.Length);
                _tlsSmallPayloadArrayCache = payloads;
                return;
            }

            if (_sharedSmallPayloadArrayCache.Count < MaxSharedCacheSize)
            {
                Array.Clear(payloads, 0, payloads.Length);
                _sharedSmallPayloadArrayCache.Add(payloads);
            }
            return;
        }

        // Large array path (same pattern)
        if (_tlsPayloadArrayCache is null)
        {
            Array.Clear(payloads, 0, payloads.Length);
            _tlsPayloadArrayCache = payloads;
            return;
        }

        if (_sharedPayloadArrayCache.Count < MaxSharedCacheSize)
        {
            Array.Clear(payloads, 0, payloads.Length);
            _sharedPayloadArrayCache.Add(payloads);
        }
    }

    private sealed class PendingOperation : IValueTaskSource<RedisRespReader.RespValue>
    {
        private ManualResetValueTaskSourceCore<RedisRespReader.RespValue> _core;
        private RedisMultiplexedConnection _owner;
        private CancellationTokenRegistration _ctr;
        private CancellationToken _ct;
        private bool _holdsSlot;
        private int _completed;
        private int _responseProcessed;
        private int _awaiterObserved;
        private CancellationTokenSource? _linkedCts;

        public PendingOperation(RedisMultiplexedConnection owner)
        {
            _owner = owner;
            _core = new ManualResetValueTaskSourceCore<RedisRespReader.RespValue>
            {
                RunContinuationsAsynchronously = true
            };
        }

        public bool PoolBulk { get; private set; }
        public bool IsCompleted => Volatile.Read(ref _completed) != 0;
        public ValueTask<RedisRespReader.RespValue> ValueTask { get; private set; }

        public void Reset(RedisMultiplexedConnection owner)
        {
            _owner = owner;
            PoolBulk = false;
            ValueTask = default;
            _ct = default;
            _holdsSlot = false;
            _linkedCts?.Dispose();
            _linkedCts = null;
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

            // Create a linked token that cancels when either the user's token or the internal _cts is cancelled
            // This ensures disposal cancels all pending operations even if user passed CancellationToken.None
            if (ct.CanBeCanceled)
            {
                _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _owner._cts.Token);
                _ctr = _linkedCts.Token.Register(static s =>
                {
                    var op = (PendingOperation)s!;
                    op.TrySetException(new OperationCanceledException(op._ct));
                }, this);
            }
            else
            {
                // User passed CancellationToken.None, so only listen to internal _cts
                _ctr = _owner._cts.Token.Register(static s =>
                {
                    var op = (PendingOperation)s!;
                    op.TrySetException(new OperationCanceledException());
                }, this);
            }
        }

        public void TrySetResult(RedisRespReader.RespValue value)
        {
            if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
                return;

            _ctr.Dispose();
            _linkedCts?.Dispose();
            _linkedCts = null;
            _core.SetResult(value);
        }

        public void TrySetException(Exception ex)
        {
            if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
                return;

            _ctr.Dispose();
            _linkedCts?.Dispose();
            _linkedCts = null;
            _core.SetException(ex);
        }

        public void MarkResponseProcessed()
        {
            Volatile.Write(ref _responseProcessed, 1);
            if (_holdsSlot)
            {
                _holdsSlot = false;
                _owner._inFlight.Release();
            }
            // Don't call TryReturnToPool() here - only return to pool after GetResult() is called
            // This prevents race condition where operation is reset before GetResult() executes
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
                _owner._inFlight.Release();
            }
            _owner.ReturnOperation(this);
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
            _owner.ReturnOperation(this);
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
            // Dispose CancellationTokenRegistration to prevent leaks
            try { _ctr.Dispose(); } catch { }
            try { _linkedCts?.Dispose(); } catch { }
            _linkedCts = null;

            // Release slot if still held (defensive)
            if (_holdsSlot)
            {
                try
                {
                    _holdsSlot = false;
                    _owner._inFlight.Release();
                }
                catch { }
            }
        }
    }

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

        public bool TryDequeue(out T? item)
        {
            if (!_items.Wait(0))
            {
                item = default;
                return false;
            }

            if (TryDequeueCore(out item))
            {
                _slots.Release();
                return true;
            }

            _items.Release();
            item = default;
            return false;
        }

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
                if (TryDequeueCore(out var item))
                {
                    _slots.Release();
                    return item!;
                }

                spinner.SpinOnce();

                if (spinner.Count > 1000)
                {
                    _items.Release();
                    throw new InvalidOperationException("Ring dequeue failed unexpectedly after 1000 spins.");
                }
            }
        }

        private bool TryDequeueCore(out T? item)
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

                // Spin wait with limit to prevent infinite loops
                spins++;
                if (spins > 100)
                {
                    // Too many spins, likely a race condition - bail out
                    item = default;
                    return false;
                }

                Thread.SpinWait(1);
            }
        }

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

        public void Clear()
        {
            while (TryDequeue(out _)) ;
        }

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

        public void Clear()
        {
            while (TryDequeue(out _)) ;
        }

        public void Dispose()
        {
            _slots?.Dispose();
            _items?.Dispose();
        }
    }
}
