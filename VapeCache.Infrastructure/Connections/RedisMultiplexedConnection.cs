using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Net.Sockets;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisMultiplexedConnection : IAsyncDisposable
{
    // Toggle to disable the experimental socket-based RESP reader; benchmarks run with stream reader for stability.
    private const bool EnableSocketRespReader = false;
    private readonly IRedisConnectionFactory _factory;
    private readonly int _maxInFlight;
    private readonly bool _coalesceWrites;
    private readonly bool _useSocketReader;

    private readonly MpscRingQueue<PendingRequest> _writes;
    private readonly SpscRingQueue<PendingOperation> _pending;

    private readonly SemaphoreSlim _inFlight;
    private readonly ConcurrentBag<PendingOperation> _opPool = new();

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writer;
    private readonly Task _reader;
    private static readonly ReadOnlyMemory<byte> CrlfMemory = "\r\n"u8.ToArray();

    // QUICK WIN #3: ThreadLocal caching to eliminate Interlocked contention
    // Each thread gets its own cache, avoiding contention on high-concurrency workloads
    [ThreadStatic] private static byte[]? _tlsHeaderCache;
    [ThreadStatic] private static byte[]? _tlsSmallHeaderCache;
    [ThreadStatic] private static ReadOnlyMemory<byte>[]? _tlsPayloadArrayCache;
    [ThreadStatic] private static ReadOnlyMemory<byte>[]? _tlsSmallPayloadArrayCache;
    private readonly Queue<CoalescedPendingRequest> _coalesceQueue = new(16);
    private readonly List<PendingRequest> _coalesceDrained = new(8);
    private readonly List<CoalescedPendingRequest> _coalesceCaptured = new(8);
    private readonly ArraySegment<byte>[] _coalesceBuffers = new ArraySegment<byte>[64];
    private int _coalesceBufferCount;
    private readonly Coalescer _coalescer;
    private readonly CoalescedWriteBatch _coalesceBatch = new();
    private readonly ReadOnlyMemory<byte>[][] _coalesceSegmentsPool8 = new ReadOnlyMemory<byte>[8][];
    private int _coalesceSegmentsPool8Count;
    private readonly ArrayPool<ReadOnlyMemory<byte>> _coalesceSegmentArrayPool = ArrayPool<ReadOnlyMemory<byte>>.Shared;
    private readonly SocketIoAwaitableEventArgs _sendArgs = new();

    private IRedisConnection? _conn;
    private RedisRespReaderState? _respReader;
    private RedisRespSocketReaderState? _respSocketReader;
    private int _disposed;


    private static int RoundUpToPowerOfTwo(int value)
    {
        value = Math.Max(2, value);
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
    }

    public RedisMultiplexedConnection(IRedisConnectionFactory factory, int maxInFlight, bool coalesceWrites)
    {
        _factory = factory;
        _maxInFlight = Math.Max(1, maxInFlight);
        _coalesceWrites = coalesceWrites;
        _useSocketReader = EnableSocketRespReader && coalesceWrites;
        _inFlight = new SemaphoreSlim(_maxInFlight, _maxInFlight);

        var capacity = RoundUpToPowerOfTwo(Math.Max(128, _maxInFlight * 4));
        _writes = new MpscRingQueue<PendingRequest>(capacity);
        _pending = new SpscRingQueue<PendingOperation>(capacity);
        _coalescer = new Coalescer(_coalesceQueue);

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

    private ValueTask<RedisRespReader.RespValue> EnqueueAfterSlot(ReadOnlyMemory<byte> command, bool poolBulk, CancellationToken ct)
    {
        var op = RentOperation();
        op.Start(poolBulk, ct, holdsSlot: true);

        var req = new PendingRequest(command, op);
        if (_writes.TryEnqueue(req))
            return op.ValueTask;

        return EnqueueWithQueueWaitAsync(req, op, ct);
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
        try
        {
            await _writes.EnqueueAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            op.AbortUnqueued(ex);
            throw;
        }
        return await op.ValueTask.ConfigureAwait(false);
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_conn is not null) return;
        var created = await _factory.CreateAsync(ct).ConfigureAwait(false);
        _conn = created.Match(static c => c, static ex => throw ex);
        if (_useSocketReader)
        {
            _respSocketReader = new RedisRespSocketReaderState(_conn.Socket, useUnsafeFastPath: false);
        }
        else
        {
            _respReader = new RedisRespReaderState(_conn.Stream, useUnsafeFastPath: false);
        }
    }

    private async Task WriterLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var req = await _writes.DequeueAsync(_cts.Token).ConfigureAwait(false);
                await EnsureConnectedAsync(_cts.Token).ConfigureAwait(false);
                var conn = _conn!;

                var shouldCoalesce = _coalesceWrites
                                     && req.Payload.Length <= 1024
                                     && req.PayloadCount == 0;

                if (shouldCoalesce)
                {
                    await SendCoalescedAsync(req, conn.Socket, _cts.Token).ConfigureAwait(false);
                }
                else
                {
                    await SendLegacyAsync(req, conn, _cts.Token).ConfigureAwait(false);
                }
                if (req.HeaderBuffer is not null)
                    ReturnHeaderBuffer(req.HeaderBuffer);
                if (req.PayloadArrayBuffer is not null)
                {
                    // Preserve length; skip clearing to avoid extra work.
                    ReturnPayloadArray(req.PayloadArrayBuffer);
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
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
            try
            {
                var next = await _pending.DequeueAsync(_cts.Token).ConfigureAwait(false);
                await EnsureConnectedAsync(_cts.Token).ConfigureAwait(false);
                RedisRespReader.RespValue resp = default;
                Exception? ex = null;
                if (_useSocketReader)
                {
                    var reader = _respSocketReader ?? throw new InvalidOperationException("RESP socket reader missing after connection established.");
                    try
                    {
                        resp = await reader.ReadAsync(next.PoolBulk, _cts.Token).ConfigureAwait(false);
                        if (resp.Kind == RedisRespReader.RespKind.Error)
                            ex = new InvalidOperationException(resp.Text ?? "Redis error");
                    }
                    catch (SocketException se) when (IsFatalSocket(se.SocketErrorCode))
                    {
                        ex = se;
                        await FailTransportAsync(se).ConfigureAwait(false);
                        continue;
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
                        resp = await reader.ReadAsync(next.PoolBulk, _cts.Token).ConfigureAwait(false);
                        if (resp.Kind == RedisRespReader.RespKind.Error)
                            ex = new InvalidOperationException(resp.Text ?? "Redis error");
                    }
                    catch (IOException ioe) when (ioe.InnerException is SocketException se && IsFatalSocket(se.SocketErrorCode))
                    {
                        ex = ioe;
                        await FailTransportAsync(ioe).ConfigureAwait(false);
                        continue;
                    }
                    catch (SocketException se) when (IsFatalSocket(se.SocketErrorCode))
                    {
                        ex = se;
                        await FailTransportAsync(se).ConfigureAwait(false);
                        continue;
                    }
                    catch (Exception e)
                    {
                        ex = e;
                    }
                }

                if (next.IsCompleted)
                {
                    if (ex is null)
                        RedisRespReader.ReturnBuffers(resp);
                }
                else if (ex is not null)
                {
                    next.TrySetException(ex);
                }
                else
                {
                    next.TrySetResult(resp);
                }

                next.MarkResponseProcessed();
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
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
        await _pending.EnqueueAsync(first.Op, ct).ConfigureAwait(false);
        var firstCoalesced = ToCoalesced(first);
        _coalesceQueue.Enqueue(firstCoalesced);
        _coalesceCaptured.Add(firstCoalesced);

        // Opportunistically drain any immediately available requests to pack into this batch.
        while (_writes.TryDequeue(out var nextReq))
        {
            _coalesceDrained.Add(nextReq);
            await _pending.EnqueueAsync(nextReq.Op, ct).ConfigureAwait(false);
            var c = ToCoalesced(nextReq);
            _coalesceQueue.Enqueue(c);
            _coalesceCaptured.Add(c);
            if (_coalesceQueue.Count >= 8) break; // small bound to avoid starving reader
        }

        var coalescer = _coalescer;
        var batch = _coalesceBatch;
        while (coalescer.TryBuildBatch(batch))
        {
            _coalesceBufferCount = batch.SegmentsToWrite.Count;
            for (var i = 0; i < _coalesceBufferCount; i++)
            {
                var mem = batch.SegmentsToWrite[i];
                if (!MemoryMarshal.TryGetArray(mem, out ArraySegment<byte> seg))
                    throw new InvalidOperationException("Non-array-backed buffer encountered in coalesced send.");
                _coalesceBuffers[i] = seg;
            }

            ct.ThrowIfCancellationRequested();
            var remaining = TotalBytes(_coalesceBuffers, _coalesceBufferCount);
            var startOffset = 0;
            while (remaining > 0)
            {
                if (startOffset > 0 && _coalesceBufferCount > 0)
                {
                    var head = _coalesceBuffers[0];
                    _coalesceBuffers[0] = new ArraySegment<byte>(head.Array!, head.Offset + startOffset, head.Count - startOffset);
                    startOffset = 0;
                }

                var sent = await SendWithArgsAsync(socket, _coalesceBuffers, _coalesceBufferCount, ct).ConfigureAwait(false);
                if (sent <= 0) throw new IOException("Socket send returned 0.");
                RedisTelemetry.BytesSent.Add(sent);
                remaining -= sent;

                var consumed = sent;
                var shift = 0;
                while (shift < _coalesceBufferCount && consumed >= _coalesceBuffers[shift].Count)
                {
                    consumed -= _coalesceBuffers[shift].Count;
                    shift++;
                }

                if (shift > 0)
                {
                    for (var j = shift; j < _coalesceBufferCount; j++)
                        _coalesceBuffers[j - shift] = _coalesceBuffers[j];
                    _coalesceBufferCount -= shift;
                }

                startOffset = consumed;
            }

            Array.Clear(_coalesceBuffers, 0, _coalesceBufferCount);
            _coalesceBufferCount = 0;
            batch.RecycleAfterSend();
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

    private static int TotalBytes(ArraySegment<byte>[] buffers, int count)
    {
        var total = 0;
        for (var i = 0; i < count; i++)
            total += buffers[i].Count;
        return total;
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

    

private async ValueTask<int> SendWithArgsAsync(Socket socket, ArraySegment<byte>[] buffers, int count, CancellationToken ct)
{
    var args = _sendArgs;
    args.ResetForOperation();
    args.SetBufferList(buffers, count);

    if (socket.SendAsync(args))
    {
        args.RegisterCancellation(ct);
        return await args.WaitAsync().ConfigureAwait(false);
    }

    // Synchronous completion: honor SocketError.
    return args.CompleteInlineOrThrow();
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

        try { _cts.Cancel(); } catch { }

        try { await _writer.ConfigureAwait(false); } catch { }
        try { await _reader.ConfigureAwait(false); } catch { }

        await FailTransportAsync(new ObjectDisposedException(nameof(RedisMultiplexedConnection))).ConfigureAwait(false);

        _cts.Dispose();
        _inFlight.Dispose();
        _coalesceBatch.Dispose();

        while (_coalesceSegmentsPool8Count > 0)
        {
            var arr = _coalesceSegmentsPool8[--_coalesceSegmentsPool8Count];
            if (arr.Length > 0)
                _coalesceSegmentArrayPool.Return(arr, clearArray: true);
        }

        if (_respReader is not null)
            await _respReader.DisposeAsync().ConfigureAwait(false);
        if (_respSocketReader is not null)
            await _respSocketReader.DisposeAsync().ConfigureAwait(false);

        if (_conn is not null)
            await _conn.DisposeAsync().ConfigureAwait(false);

        // CRITICAL: Drain and dispose all pooled operations to prevent SemaphoreSlim/CancellationTokenRegistration leaks
        while (_opPool.TryTake(out var pooledOp))
        {
            try
            {
                // Ensure operation is fully completed before disposal
                if (!pooledOp.IsCompleted)
                {
                    pooledOp.TrySetException(new ObjectDisposedException(nameof(RedisMultiplexedConnection)));
                }
                pooledOp.DisposeResources();
            }
            catch { }
        }

        // CRITICAL: Dispose ring queues to prevent SemaphoreSlim leaks
        try { _writes.Dispose(); } catch { }
        try { _pending.Dispose(); } catch { }
    }

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
        // QUICK WIN #3: ThreadLocal cache - zero contention, faster than Interlocked.Exchange
        byte[]? buf;
        if (minLength <= 512)
        {
            buf = _tlsSmallHeaderCache;
            if (buf is not null && buf.Length >= minLength)
            {
                _tlsSmallHeaderCache = null;
                return buf;
            }
        }

        buf = _tlsHeaderCache;
        if (buf is not null && buf.Length >= minLength)
        {
            _tlsHeaderCache = null;
            return buf;
        }

        return new byte[minLength];
    }

    internal void ReturnHeaderBuffer(byte[] buffer)
    {
        if (buffer is null) return;

        // QUICK WIN #3: ThreadLocal return - zero contention
        if (buffer.Length <= 512)
        {
            if (_tlsSmallHeaderCache is null)
                _tlsSmallHeaderCache = buffer;
            return;
        }

        if (_tlsHeaderCache is null)
            _tlsHeaderCache = buffer;
    }

    internal ReadOnlyMemory<byte>[] RentPayloadArray(int minLength)
    {
        // QUICK WIN #3: ThreadLocal cache for payload arrays
        ReadOnlyMemory<byte>[]? arr;
        if (minLength <= 16)
        {
            arr = _tlsSmallPayloadArrayCache;
            if (arr is not null && arr.Length >= minLength)
            {
                _tlsSmallPayloadArrayCache = null;
                return arr;
            }
        }

        arr = _tlsPayloadArrayCache;
        if (arr is not null && arr.Length >= minLength)
        {
            _tlsPayloadArrayCache = null;
            return arr;
        }

        return new ReadOnlyMemory<byte>[minLength];
    }

    internal void ReturnPayloadArray(ReadOnlyMemory<byte>[]? payloads)
    {
        if (payloads is null) return;

        // QUICK WIN #3: ThreadLocal return for payload arrays
        if (payloads.Length <= 16)
        {
            if (_tlsSmallPayloadArrayCache is null)
                _tlsSmallPayloadArrayCache = payloads;
            return;
        }

        if (_tlsPayloadArrayCache is null)
            _tlsPayloadArrayCache = payloads;
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
            }
        }

        public void TrySetResult(RedisRespReader.RespValue value)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;

            _ctr.Dispose();
            _core.SetResult(value);
        }

        public void TrySetException(Exception ex)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;

            _ctr.Dispose();
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
            TryReturnToPool();
        }

        public void AbortUnqueued(Exception ex)
        {
            TrySetException(ex);
            Volatile.Write(ref _awaiterObserved, 1);
            MarkResponseProcessed();
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

        public void DisposeResources()
        {
            // Dispose CancellationTokenRegistration to prevent leaks
            try { _ctr.Dispose(); } catch { }

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
            return wait.IsCompletedSuccessfully
                ? ValueTask.CompletedTask // already acquired
                : EnqueueAsyncSlow(item, wait);
        }

        private async ValueTask EnqueueAsyncSlow(T item, Task slotWait)
        {
            await slotWait.ConfigureAwait(false);
            EnqueueAfterSlot(item);
        }

        private void EnqueueAfterSlot(T item)
        {
            if (!TryEnqueueCore(item))
            {
                _slots.Release();
                throw new InvalidOperationException("Ring enqueue failed unexpectedly.");
            }
            _items.Release();
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
            if (!TryDequeueCore(out var item))
                throw new InvalidOperationException("Ring dequeue failed unexpectedly.");

            _slots.Release();
            return item!;
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
                    item = _buffer[idx];
                    _buffer[idx] = default!;
                    Volatile.Write(ref _sequence[idx], pos + _buffer.Length);
                    Volatile.Write(ref _tail, pos + 1);
                    return true;
                }

                if (dif < 0)
                {
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
            return wait.IsCompletedSuccessfully
                ? ValueTask.CompletedTask
                : EnqueueAsyncSlow(item, wait);
        }

        private async ValueTask EnqueueAsyncSlow(T item, Task wait)
        {
            await wait.ConfigureAwait(false);
            EnqueueAfterSlot(item);
        }

        private void EnqueueAfterSlot(T item)
        {
            var idx = _head & _mask;
            _buffer[idx] = item;
            _head++;
            _items.Release();
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
