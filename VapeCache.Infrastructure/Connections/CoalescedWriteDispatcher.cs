using System.Collections;
using System.Buffers;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VapeCache.Infrastructure.Connections;

internal delegate bool TryDequeuePendingRequestDelegate(out PendingRequest request);
internal delegate int GetWriteQueueDepthDelegate();
internal delegate ValueTask EnqueuePendingOperationDelegate(PendingOperation operation, CancellationToken ct);
internal delegate long NextPendingSequenceDelegate();
internal delegate void ReturnHeaderBufferDelegate(byte[] buffer);
internal delegate void ReturnPayloadArrayDelegate(ReadOnlyMemory<byte>[]? payloads);
internal delegate void AbortPendingRequestDelegate(PendingRequest request, Exception ex);

internal sealed class CoalescedWriteDispatcher : IDisposable
{
    private readonly Queue<CoalescedPendingRequest> _coalesceQueue = new(16);
    private readonly List<PendingRequest> _coalesceDrained = new(8);
    private readonly List<CoalescedPendingRequest> _coalesceCaptured = new(8);
    private readonly Coalescer _coalescer;
    private readonly CoalescedWriteBatch _coalesceBatch = new();
    private readonly SocketSendSegmentWindow _socketSendWindow = new();
    private readonly ReadOnlyMemory<byte>[][] _coalesceSegmentsPool8 = new ReadOnlyMemory<byte>[8][];
    private int _coalesceSegmentsPool8Count;
    private readonly ArrayPool<ReadOnlyMemory<byte>> _coalesceSegmentArrayPool = ArrayPool<ReadOnlyMemory<byte>>.Shared;
    private readonly ArrayPool<ArraySegment<byte>> _socketSendSegmentArrayPool = ArrayPool<ArraySegment<byte>>.Shared;
    private readonly ReadOnlyMemory<byte> _crlfMemory;

    private readonly TryDequeuePendingRequestDelegate _tryDequeueWrite;
    private readonly GetWriteQueueDepthDelegate _getWriteQueueDepth;
    private readonly EnqueuePendingOperationDelegate _enqueuePendingOperation;
    private readonly NextPendingSequenceDelegate _nextPendingSequence;
    private readonly ReturnHeaderBufferDelegate _returnHeaderBuffer;
    private readonly ReturnPayloadArrayDelegate _returnPayloadArray;
    private readonly AbortPendingRequestDelegate _abortPendingRequest;
    private readonly Action<int>? _recordBytesSent;

    public CoalescedWriteDispatcher(
        int coalescedWriteMaxBytes,
        int coalescedWriteMaxSegments,
        int coalescedWriteSmallCopyThresholdBytes,
        bool enableAdaptiveCoalescing,
        int adaptiveCoalescingLowDepth,
        int adaptiveCoalescingHighDepth,
        int adaptiveCoalescingMinWriteBytes,
        int adaptiveCoalescingMinSegments,
        int adaptiveCoalescingMinSmallCopyThresholdBytes,
        ReadOnlyMemory<byte> crlfMemory,
        TryDequeuePendingRequestDelegate tryDequeueWrite,
        GetWriteQueueDepthDelegate getWriteQueueDepth,
        EnqueuePendingOperationDelegate enqueuePendingOperation,
        NextPendingSequenceDelegate nextPendingSequence,
        ReturnHeaderBufferDelegate returnHeaderBuffer,
        ReturnPayloadArrayDelegate returnPayloadArray,
        AbortPendingRequestDelegate abortPendingRequest,
        Action<int>? recordBytesSent = null)
    {
        _crlfMemory = crlfMemory;
        _tryDequeueWrite = tryDequeueWrite;
        _getWriteQueueDepth = getWriteQueueDepth;
        _enqueuePendingOperation = enqueuePendingOperation;
        _nextPendingSequence = nextPendingSequence;
        _returnHeaderBuffer = returnHeaderBuffer;
        _returnPayloadArray = returnPayloadArray;
        _abortPendingRequest = abortPendingRequest;
        _recordBytesSent = recordBytesSent;
        _coalescer = new Coalescer(
            _coalesceQueue,
            coalescedWriteMaxBytes,
            coalescedWriteMaxSegments,
            coalescedWriteSmallCopyThresholdBytes,
            enableAdaptiveCoalescing,
            adaptiveCoalescingLowDepth,
            adaptiveCoalescingHighDepth,
            adaptiveCoalescingMinWriteBytes,
            adaptiveCoalescingMinSegments,
            adaptiveCoalescingMinSmallCopyThresholdBytes,
            () => _getWriteQueueDepth());
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async Task SendAsync(PendingRequest first, Socket socket, long generation, CancellationToken ct)
    {
        _coalesceQueue.Clear();
        _coalesceDrained.Clear();
        _coalesceCaptured.Clear();
        _coalesceDrained.Add(first);
        var queuedDepth = _getWriteQueueDepth();
        var drainLimit = GetTailAwareDrainLimit(queuedDepth);

        var firstCoalesced = ToCoalesced(first);
        _coalesceQueue.Enqueue(firstCoalesced);
        _coalesceCaptured.Add(firstCoalesced);

        while (_tryDequeueWrite(out var nextReq))
        {
            _coalesceDrained.Add(nextReq);
            var coalesced = ToCoalesced(nextReq);
            _coalesceQueue.Enqueue(coalesced);
            _coalesceCaptured.Add(coalesced);
            if (_coalesceQueue.Count >= drainLimit) break;
        }

        var requestCommitOffsets = BuildRequestCommitOffsets(_coalesceDrained);
        var enqueuedToPending = 0;
        long committedBytes = 0;

        async ValueTask CommitSentBytesAsync(int sentBytes)
        {
            if (sentBytes <= 0)
                return;

            committedBytes += sentBytes;
            RedisTelemetry.BytesSent.Add(sentBytes);
            _recordBytesSent?.Invoke(sentBytes);

            while (enqueuedToPending < requestCommitOffsets.Length &&
                   committedBytes >= requestCommitOffsets[enqueuedToPending])
            {
                var op = _coalesceDrained[enqueuedToPending].Op;
                op.AssignGeneration(generation);
                op.AssignSequenceId(_nextPendingSequence());
                await _enqueuePendingOperation(op, ct).ConfigureAwait(false);
                enqueuedToPending++;
            }
        }

        try
        {
            while (_coalescer.TryBuildBatch(_coalesceBatch))
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var totalBytes = 0;
                    for (var i = 0; i < _coalesceBatch.SegmentsToWrite.Count; i++)
                        totalBytes += _coalesceBatch.SegmentsToWrite[i].Length;

                    if (totalBytes > 0)
                    {
                        RedisTelemetry.CoalescedWriteBatches.Add(1);
                        RedisTelemetry.CoalescedWriteBatchBytes.Record(totalBytes);
                        RedisTelemetry.CoalescedWriteBatchSegments.Record(_coalesceBatch.SegmentsToWrite.Count);

                        if (!await TrySendVectoredAsync(socket, _coalesceBatch.SegmentsToWrite, CommitSentBytesAsync, ct).ConfigureAwait(false))
                        {
                            var rented = ArrayPool<byte>.Shared.Rent(totalBytes);
                            try
                            {
                                var offset = 0;
                                for (var i = 0; i < _coalesceBatch.SegmentsToWrite.Count; i++)
                                {
                                    var segment = _coalesceBatch.SegmentsToWrite[i];
                                    segment.CopyTo(rented.AsMemory(offset, segment.Length));
                                    offset += segment.Length;
                                }

                                await SendAllAsync(socket, rented.AsMemory(0, totalBytes), CommitSentBytesAsync, ct).ConfigureAwait(false);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(rented);
                            }
                        }
                    }
                }
                finally
                {
                    _coalesceBatch.RecycleAfterSend();
                }
            }
        }
        catch (Exception ex)
        {
            for (var i = enqueuedToPending; i < _coalesceDrained.Count; i++)
                _abortPendingRequest(_coalesceDrained[i], ex);
            throw;
        }
        finally
        {
            for (var i = 0; i < _coalesceDrained.Count; i++)
            {
                var req = _coalesceDrained[i];
                if (req.HeaderBuffer is not null)
                    _returnHeaderBuffer(req.HeaderBuffer);
                if (req.PayloadArrayBuffer is not null)
                    _returnPayloadArray(req.PayloadArrayBuffer);
            }

            for (var i = 0; i < _coalesceCaptured.Count; i++)
                ReturnCoalesceSegments(_coalesceCaptured[i].Segments);
        }
    }

    private long[] BuildRequestCommitOffsets(List<PendingRequest> requests)
    {
        var offsets = new long[requests.Count];
        long running = 0;
        for (var i = 0; i < requests.Count; i++)
        {
            running += GetRequestWireLength(requests[i]);
            offsets[i] = running;
        }

        return offsets;
    }

    /// <summary>
    /// Releases resources used by the current instance.
    /// </summary>
    public void Dispose()
    {
        _coalesceBatch.Dispose();
        while (_coalesceSegmentsPool8Count > 0)
        {
            var arr = _coalesceSegmentsPool8[--_coalesceSegmentsPool8Count];
            if (arr.Length > 0)
                _coalesceSegmentArrayPool.Return(arr, clearArray: true);
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
            count += req.AppendCrlfPerPayload ? req.PayloadCount * 2 : req.PayloadCount;
        }

        var segments = RentCoalesceSegments(count);
        var idx = 0;
        segments[idx++] = req.Command;

        if (!req.Payload.IsEmpty)
        {
            segments[idx++] = req.Payload;
            if (req.AppendCrlf)
                segments[idx++] = _crlfMemory;
        }
        else if (req.PayloadCount > 0 && req.Payloads is not null)
        {
            for (var i = 0; i < req.PayloadCount; i++)
            {
                segments[idx++] = req.Payloads[i];
                if (req.AppendCrlfPerPayload)
                    segments[idx++] = _crlfMemory;
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

    private long GetRequestWireLength(PendingRequest request)
    {
        long total = request.Command.Length;

        if (!request.Payload.IsEmpty)
        {
            total += request.Payload.Length;
            if (request.AppendCrlf)
                total += _crlfMemory.Length;
            return total;
        }

        if (request.PayloadCount <= 0 || request.Payloads is null)
            return total;

        for (var i = 0; i < request.PayloadCount; i++)
        {
            total += request.Payloads[i].Length;
            if (request.AppendCrlfPerPayload)
                total += _crlfMemory.Length;
        }

        return total;
    }

    private static async ValueTask SendAllAsync(
        Socket socket,
        ReadOnlyMemory<byte> buffer,
        Func<int, ValueTask> onBytesCommitted,
        CancellationToken ct)
    {
        while (!buffer.IsEmpty)
        {
            var sent = await socket.SendAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
            if (sent <= 0)
                throw new IOException("Socket send returned 0.");
            await onBytesCommitted(sent).ConfigureAwait(false);
            buffer = buffer.Slice(sent);
        }
    }

    // Uses vectored socket sends when all segments are array-backed. Falls back to copy-send otherwise.
    private async ValueTask<bool> TrySendVectoredAsync(
        Socket socket,
        List<ReadOnlyMemory<byte>> segments,
        Func<int, ValueTask> onBytesCommitted,
        CancellationToken ct)
    {
        if (segments.Count == 0)
            return true;

        var sendSegments = _socketSendSegmentArrayPool.Rent(segments.Count);
        var sendCount = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (seg.IsEmpty)
                continue;

            if (!MemoryMarshal.TryGetArray(seg, out ArraySegment<byte> arraySegment))
            {
                Array.Clear(sendSegments, 0, sendCount);
                _socketSendSegmentArrayPool.Return(sendSegments, clearArray: false);
                return false;
            }

            sendSegments[sendCount++] = arraySegment;
        }

        if (sendCount == 0)
        {
            _socketSendSegmentArrayPool.Return(sendSegments, clearArray: false);
            return true;
        }

        try
        {
            _socketSendWindow.Reset(sendSegments, sendCount);
            while (_socketSendWindow.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var sent = await socket.SendAsync(_socketSendWindow, SocketFlags.None).ConfigureAwait(false);
                if (sent <= 0)
                    throw new IOException("Socket send returned 0.");
                await onBytesCommitted(sent).ConfigureAwait(false);
                _socketSendWindow.Consume(sent);
            }

            return true;
        }
        finally
        {
            _socketSendWindow.Reset();
            Array.Clear(sendSegments, 0, sendCount);
            _socketSendSegmentArrayPool.Return(sendSegments, clearArray: false);
        }
    }

    private static int GetTailAwareDrainLimit(int writeQueueDepth)
    {
        if (writeQueueDepth >= 1024) return 1;
        if (writeQueueDepth >= 512) return 2;
        if (writeQueueDepth >= 256) return 3;
        if (writeQueueDepth >= 128) return 4;
        if (writeQueueDepth >= 64) return 5;
        if (writeQueueDepth >= 32) return 6;
        return 7;
    }

    private sealed class SocketSendSegmentWindow : IList<ArraySegment<byte>>
    {
        private ArraySegment<byte>[]? _segments;
        private int _head;
        private int _count;

        public int Count => _count;

        public bool IsReadOnly => true;

        public ArraySegment<byte> this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_count || _segments is null)
                    throw new ArgumentOutOfRangeException(nameof(index));

                return _segments[_head + index];
            }
            set => throw new NotSupportedException();
        }

        public void Reset(ArraySegment<byte>[]? segments = null, int count = 0)
        {
            _segments = segments;
            _head = 0;
            _count = count;
        }

        public void Consume(int sent)
        {
            while (sent > 0 && _count > 0)
            {
                ref var headSegment = ref _segments![_head];
                if (sent >= headSegment.Count)
                {
                    sent -= headSegment.Count;
                    headSegment = default;
                    _head++;
                    _count--;
                    continue;
                }

                headSegment = new ArraySegment<byte>(headSegment.Array!, headSegment.Offset + sent, headSegment.Count - sent);
                sent = 0;
            }

            if (_count == 0)
                _head = 0;
        }

        public IEnumerator<ArraySegment<byte>> GetEnumerator()
        {
            for (var i = 0; i < _count; i++)
                yield return _segments![_head + i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public bool Contains(ArraySegment<byte> item)
        {
            for (var i = 0; i < _count; i++)
            {
                if (_segments![_head + i].Equals(item))
                    return true;
            }

            return false;
        }

        public int IndexOf(ArraySegment<byte> item)
        {
            for (var i = 0; i < _count; i++)
            {
                if (_segments![_head + i].Equals(item))
                    return i;
            }

            return -1;
        }

        public void CopyTo(ArraySegment<byte>[] array, int arrayIndex)
        {
            if (_count == 0)
                return;

            Array.Copy(_segments!, _head, array, arrayIndex, _count);
        }

        public void Add(ArraySegment<byte> item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public void Insert(int index, ArraySegment<byte> item) => throw new NotSupportedException();
        public bool Remove(ArraySegment<byte> item) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
    }
}
