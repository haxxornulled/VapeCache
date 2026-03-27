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
    private const int MaxVectoredSegmentsPerSend = 64;

    private readonly Queue<CoalescedPendingRequest> _coalesceQueue = new(16);
    private readonly List<PendingRequest> _coalesceDrained = new(8);
    private readonly List<CoalescedPendingRequest> _coalesceCaptured = new(8);
    private readonly Coalescer _coalescer;
    private readonly CoalescedWriteBatch _coalesceBatch = new();
    private readonly SocketSendSegmentWindow _socketSendWindow = new();
    private readonly SocketIoAwaitableEventArgs _sendArgs = new();
    private readonly ReadOnlyMemory<byte>[][] _coalesceSegmentsPool8 = new ReadOnlyMemory<byte>[8][];
    private int _coalesceSegmentsPool8Count;
    private readonly long[][] _commitOffsetsPool8 = new long[8][];
    private int _commitOffsetsPool8Count;
    private readonly ArrayPool<ReadOnlyMemory<byte>> _coalesceSegmentArrayPool = ArrayPool<ReadOnlyMemory<byte>>.Shared;
    private readonly ArrayPool<ArraySegment<byte>> _socketSendSegmentArrayPool = ArrayPool<ArraySegment<byte>>.Shared;
    private readonly ArrayPool<long> _commitOffsetsArrayPool = ArrayPool<long>.Shared;
    private readonly ReadOnlyMemory<byte> _crlfMemory;

    private readonly TryDequeuePendingRequestDelegate _tryDequeueWrite;
    private readonly GetWriteQueueDepthDelegate _getWriteQueueDepth;
    private readonly EnqueuePendingOperationDelegate _enqueuePendingOperation;
    private readonly NextPendingSequenceDelegate _nextPendingSequence;
    private readonly ReturnHeaderBufferDelegate _returnHeaderBuffer;
    private readonly ReturnPayloadArrayDelegate _returnPayloadArray;
    private readonly AbortPendingRequestDelegate _abortPendingRequest;
    private readonly Action<int>? _recordBytesSent;
    private readonly int _coalescingEnterQueueDepth;
    private readonly int _coalescingExitQueueDepth;
    private readonly int _coalescingSpinBudget;
    private bool _burstCoalescingActive;

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
        Action<int>? recordBytesSent = null,
        int coalescingEnterQueueDepth = 8,
        int coalescingExitQueueDepth = 3,
        int coalescedWriteMaxOperations = 128,
        int coalescingSpinBudget = 8)
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
        _coalescingEnterQueueDepth = Math.Max(1, coalescingEnterQueueDepth);
        _coalescingExitQueueDepth = Math.Clamp(coalescingExitQueueDepth, 1, _coalescingEnterQueueDepth);
        _coalescingSpinBudget = Math.Max(0, coalescingSpinBudget);
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
            coalescedWriteMaxOperations,
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
        var burstMode = UpdateBurstCoalescingMode(queuedDepth);
        var drainLimit = GetTailAwareDrainLimit(queuedDepth);

        var firstCoalesced = ToCoalesced(in first);
        _coalesceQueue.Enqueue(firstCoalesced);
        _coalesceCaptured.Add(firstCoalesced);

        var drainedFollower = false;
        var spinAttempts = 0;
        while (true)
        {
            if (!_tryDequeueWrite(out var nextReq))
            {
                // Flush immediately when this was a lone request.
                if (!burstMode || !drainedFollower || spinAttempts >= _coalescingSpinBudget)
                    break;

                Thread.SpinWait(64 << Math.Min(spinAttempts, 6));
                spinAttempts++;
                continue;
            }

            drainedFollower = true;
            spinAttempts = 0;
            _coalesceDrained.Add(nextReq);
            var coalesced = ToCoalesced(in nextReq);
            _coalesceQueue.Enqueue(coalesced);
            _coalesceCaptured.Add(coalesced);
            if (_coalesceQueue.Count >= drainLimit)
                break;
        }

        var requestCount = _coalesceDrained.Count;
        var requestCommitOffsets = RentRequestCommitOffsets(requestCount);
        BuildRequestCommitOffsets(_coalesceDrained, requestCommitOffsets);
        var commitTracker = new SendCommitTracker(
            _coalesceDrained,
            requestCommitOffsets,
            requestCount,
            generation,
            _enqueuePendingOperation,
            _nextPendingSequence,
            _recordBytesSent);

        try
        {
            while (_coalescer.TryBuildBatch(_coalesceBatch))
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var segmentsToWrite = _coalesceBatch.SegmentsToWrite;
                    var segmentCount = segmentsToWrite.Count;
                    var totalBytes = 0;
                    for (var i = 0; i < segmentCount; i++)
                        totalBytes += segmentsToWrite[i].Length;

                    if (totalBytes > 0)
                    {
                        RedisTelemetry.CoalescedWriteBatches.Add(1);
                        RedisTelemetry.CoalescedWriteBatchBytes.Record(totalBytes);
                        RedisTelemetry.CoalescedWriteBatchSegments.Record(segmentCount);

                        var vectoredResult = await TrySendVectoredAsync(socket, _coalesceBatch.SegmentsToWrite, commitTracker, ct).ConfigureAwait(false);
                        commitTracker = vectoredResult.Tracker;
                        if (!vectoredResult.Success)
                        {
                            var rented = ArrayPool<byte>.Shared.Rent(totalBytes);
                            try
                            {
                                var offset = 0;
                                for (var i = 0; i < segmentCount; i++)
                                {
                                    var segment = segmentsToWrite[i];
                                    segment.CopyTo(rented.AsMemory(offset, segment.Length));
                                    offset += segment.Length;
                                }

                                commitTracker = await SendAllAsync(socket, rented.AsMemory(0, totalBytes), commitTracker, ct).ConfigureAwait(false);
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
            var transportFailure = NormalizeTransportException(ex);
            for (var i = commitTracker.EnqueuedCount; i < _coalesceDrained.Count; i++)
                _abortPendingRequest(_coalesceDrained[i], transportFailure);
            throw transportFailure;
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

            ReturnRequestCommitOffsets(requestCommitOffsets);
        }
    }

    private void BuildRequestCommitOffsets(List<PendingRequest> requests, long[] offsets)
    {
        var requestSpan = CollectionsMarshal.AsSpan(requests);
        long running = 0;
        for (var i = 0; i < requestSpan.Length; i++)
        {
            running += GetRequestWireLength(in requestSpan[i]);
            offsets[i] = running;
        }
    }

    private long[] RentRequestCommitOffsets(int minLength)
    {
        if (minLength <= 8 && _commitOffsetsPool8Count > 0)
        {
            var idx = --_commitOffsetsPool8Count;
            var cached = _commitOffsetsPool8[idx];
            _commitOffsetsPool8[idx] = Array.Empty<long>();
            if (cached.Length >= minLength)
                return cached;
            _commitOffsetsArrayPool.Return(cached, clearArray: false);
        }

        var size = minLength <= 8 ? 8 : minLength;
        return _commitOffsetsArrayPool.Rent(size);
    }

    private void ReturnRequestCommitOffsets(long[] offsets)
    {
        if (offsets.Length == 8 && _commitOffsetsPool8Count < _commitOffsetsPool8.Length)
        {
            _commitOffsetsPool8[_commitOffsetsPool8Count++] = offsets;
            return;
        }

        _commitOffsetsArrayPool.Return(offsets, clearArray: false);
    }

    /// <summary>
    /// Releases resources used by the current instance.
    /// </summary>
    public void Dispose()
    {
        _coalesceBatch.Dispose();
        _sendArgs.Dispose();
        while (_coalesceSegmentsPool8Count > 0)
        {
            var arr = _coalesceSegmentsPool8[--_coalesceSegmentsPool8Count];
            if (arr.Length > 0)
                _coalesceSegmentArrayPool.Return(arr, clearArray: true);
        }

        while (_commitOffsetsPool8Count > 0)
        {
            var arr = _commitOffsetsPool8[--_commitOffsetsPool8Count];
            if (arr.Length > 0)
                _commitOffsetsArrayPool.Return(arr, clearArray: false);
        }
    }

    private CoalescedPendingRequest ToCoalesced(in PendingRequest req)
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

    private long GetRequestWireLength(in PendingRequest request)
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

    private static async ValueTask<SendCommitTracker> SendAllAsync(
        Socket socket,
        ReadOnlyMemory<byte> buffer,
        SendCommitTracker commitTracker,
        CancellationToken ct)
    {
        while (!buffer.IsEmpty)
        {
            int sent;
            try
            {
                sent = await socket.SendAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw NormalizeTransportException(ex);
            }

            if (sent <= 0)
                throw new IOException("Socket send returned 0.");
            await commitTracker.CommitAsync(sent, ct).ConfigureAwait(false);
            buffer = buffer.Slice(sent);
        }

        return commitTracker;
    }

    // Uses vectored socket sends when all segments are array-backed. Falls back to copy-send otherwise.
    private async ValueTask<VectoredSendResult> TrySendVectoredAsync(
        Socket socket,
        List<ReadOnlyMemory<byte>> segments,
        SendCommitTracker commitTracker,
        CancellationToken ct)
    {
        if (segments.Count == 0)
            return new(success: true, commitTracker);

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
                return new(success: false, commitTracker);
            }

            sendSegments[sendCount++] = arraySegment;
        }

        if (sendCount == 0)
        {
            _socketSendSegmentArrayPool.Return(sendSegments, clearArray: false);
            return new(success: true, commitTracker);
        }

        try
        {
            _socketSendWindow.Reset(sendSegments, sendCount);
            while (_socketSendWindow.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                int sent;
                try
                {
                    _sendArgs.ResetForOperation();
                    var sendWindowCount = Math.Min(_socketSendWindow.Count, MaxVectoredSegmentsPerSend);
                    _sendArgs.SetBufferList(sendSegments, _socketSendWindow.Head, sendWindowCount);
                    if (socket.SendAsync(_sendArgs))
                    {
                        _sendArgs.RegisterCancellation(ct);
                        sent = await _sendArgs.WaitAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        sent = _sendArgs.CompleteInlineOrThrow();
                    }
                }
                catch (Exception ex)
                {
                    throw NormalizeTransportException(ex);
                }

                if (sent <= 0)
                    throw new IOException("Socket send returned 0.");
                await commitTracker.CommitAsync(sent, ct).ConfigureAwait(false);
                _socketSendWindow.Consume(sent);
            }

            return new(success: true, commitTracker);
        }
        finally
        {
            _socketSendWindow.Reset();
            Array.Clear(sendSegments, 0, sendCount);
            _socketSendSegmentArrayPool.Return(sendSegments, clearArray: false);
        }
    }

    private readonly struct VectoredSendResult
    {
        public VectoredSendResult(bool success, SendCommitTracker tracker)
        {
            Success = success;
            Tracker = tracker;
        }

        public bool Success { get; }
        public SendCommitTracker Tracker { get; }
    }

    private struct SendCommitTracker
    {
        private readonly List<PendingRequest> _requests;
        private readonly long[] _requestCommitOffsets;
        private readonly int _requestCount;
        private readonly long _generation;
        private readonly EnqueuePendingOperationDelegate _enqueuePendingOperation;
        private readonly NextPendingSequenceDelegate _nextPendingSequence;
        private readonly Action<int>? _recordBytesSent;
        private long _committedBytes;
        private int _enqueuedCount;

        public SendCommitTracker(
            List<PendingRequest> requests,
            long[] requestCommitOffsets,
            int requestCount,
            long generation,
            EnqueuePendingOperationDelegate enqueuePendingOperation,
            NextPendingSequenceDelegate nextPendingSequence,
            Action<int>? recordBytesSent)
        {
            _requests = requests;
            _requestCommitOffsets = requestCommitOffsets;
            _requestCount = requestCount;
            _generation = generation;
            _enqueuePendingOperation = enqueuePendingOperation;
            _nextPendingSequence = nextPendingSequence;
            _recordBytesSent = recordBytesSent;
            _committedBytes = 0;
            _enqueuedCount = 0;
        }

        public int EnqueuedCount => _enqueuedCount;

        public ValueTask CommitAsync(int sentBytes, CancellationToken ct)
        {
            if (sentBytes <= 0)
                return ValueTask.CompletedTask;

            _committedBytes += sentBytes;
            RedisTelemetry.BytesSent.Add(sentBytes);
            _recordBytesSent?.Invoke(sentBytes);

            while (_enqueuedCount < _requestCount &&
                   _committedBytes >= _requestCommitOffsets[_enqueuedCount])
            {
                var op = _requests[_enqueuedCount].Op;
                op.AssignGeneration(_generation);
                op.AssignSequenceId(_nextPendingSequence());
                var enqueue = _enqueuePendingOperation(op, ct);
                if (!enqueue.IsCompletedSuccessfully)
                    return CommitSlowAsync(enqueue, ct);
                _enqueuedCount++;
            }

            return ValueTask.CompletedTask;
        }

        private async ValueTask CommitSlowAsync(ValueTask firstEnqueue, CancellationToken ct)
        {
            await firstEnqueue.ConfigureAwait(false);
            _enqueuedCount++;

            while (_enqueuedCount < _requestCount &&
                   _committedBytes >= _requestCommitOffsets[_enqueuedCount])
            {
                var op = _requests[_enqueuedCount].Op;
                op.AssignGeneration(_generation);
                op.AssignSequenceId(_nextPendingSequence());
                await _enqueuePendingOperation(op, ct).ConfigureAwait(false);
                _enqueuedCount++;
            }
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

    private bool UpdateBurstCoalescingMode(int queueDepth)
    {
        if (_burstCoalescingActive)
        {
            if (queueDepth <= _coalescingExitQueueDepth)
                _burstCoalescingActive = false;
        }
        else if (queueDepth >= _coalescingEnterQueueDepth)
        {
            _burstCoalescingActive = true;
        }

        return _burstCoalescingActive;
    }

    private static Exception NormalizeTransportException(Exception ex)
    {
        if (ex is ObjectDisposedException)
            return new IOException("Socket was disposed during coalesced write send.", ex);

        if (ex is SocketException se &&
            (se.SocketErrorCode == SocketError.OperationAborted ||
             se.SocketErrorCode == SocketError.ConnectionReset ||
             se.SocketErrorCode == SocketError.ConnectionAborted ||
             se.SocketErrorCode == SocketError.NotConnected))
        {
            return new IOException($"Socket write failed: {se.SocketErrorCode}.", se);
        }

        return ex;
    }

    private sealed class SocketSendSegmentWindow : IList<ArraySegment<byte>>
    {
        private ArraySegment<byte>[]? _segments;
        private int _head;
        private int _count;

        public int Count => _count;
        public int Head => _head;

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
