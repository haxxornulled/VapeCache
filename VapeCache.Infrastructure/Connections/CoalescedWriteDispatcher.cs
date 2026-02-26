using System.Buffers;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VapeCache.Infrastructure.Connections;

internal delegate bool TryDequeuePendingRequestDelegate(out PendingRequest request);
internal delegate int GetWriteQueueDepthDelegate();
internal delegate ValueTask EnqueuePendingOperationDelegate(PendingOperation operation, CancellationToken ct);
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
    private readonly ReadOnlyMemory<byte>[][] _coalesceSegmentsPool8 = new ReadOnlyMemory<byte>[8][];
    private int _coalesceSegmentsPool8Count;
    private readonly ArrayPool<ReadOnlyMemory<byte>> _coalesceSegmentArrayPool = ArrayPool<ReadOnlyMemory<byte>>.Shared;
    private readonly ReadOnlyMemory<byte> _crlfMemory;

    private readonly TryDequeuePendingRequestDelegate _tryDequeueWrite;
    private readonly GetWriteQueueDepthDelegate _getWriteQueueDepth;
    private readonly EnqueuePendingOperationDelegate _enqueuePendingOperation;
    private readonly ReturnHeaderBufferDelegate _returnHeaderBuffer;
    private readonly ReturnPayloadArrayDelegate _returnPayloadArray;
    private readonly AbortPendingRequestDelegate _abortPendingRequest;

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
        ReturnHeaderBufferDelegate returnHeaderBuffer,
        ReturnPayloadArrayDelegate returnPayloadArray,
        AbortPendingRequestDelegate abortPendingRequest)
    {
        _crlfMemory = crlfMemory;
        _tryDequeueWrite = tryDequeueWrite;
        _getWriteQueueDepth = getWriteQueueDepth;
        _enqueuePendingOperation = enqueuePendingOperation;
        _returnHeaderBuffer = returnHeaderBuffer;
        _returnPayloadArray = returnPayloadArray;
        _abortPendingRequest = abortPendingRequest;
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

    public async Task SendAsync(PendingRequest first, Socket socket, CancellationToken ct)
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

        var enqueuedToPending = 0;
        try
        {
            // Register pending response operations before writing bytes so the reader loop
            // can start consuming replies immediately as Redis responds.
            for (var i = 0; i < _coalesceDrained.Count; i++)
            {
                await _enqueuePendingOperation(_coalesceDrained[i].Op, ct).ConfigureAwait(false);
                enqueuedToPending++;
            }

            while (_coalescer.TryBuildBatch(_coalesceBatch))
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

                    if (!await TrySendVectoredAsync(socket, _coalesceBatch.SegmentsToWrite, ct).ConfigureAwait(false))
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

                            await SendAllAsync(socket, rented.AsMemory(0, totalBytes), ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(rented);
                        }
                    }

                    RedisTelemetry.BytesSent.Add(totalBytes);
                }

                _coalesceBatch.RecycleAfterSend();
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

    // Uses vectored socket sends when all segments are array-backed. Falls back to copy-send otherwise.
    private static async ValueTask<bool> TrySendVectoredAsync(
        Socket socket,
        List<ReadOnlyMemory<byte>> segments,
        CancellationToken ct)
    {
        if (segments.Count == 0)
            return true;

        var sendList = new List<ArraySegment<byte>>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (seg.IsEmpty)
                continue;

            if (!MemoryMarshal.TryGetArray(seg, out ArraySegment<byte> arraySegment))
                return false;

            sendList.Add(arraySegment);
        }

        while (sendList.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var sent = await socket.SendAsync(sendList, SocketFlags.None).ConfigureAwait(false);
            if (sent <= 0)
                throw new IOException("Socket send returned 0.");

            ConsumeSent(sendList, sent);
        }

        return true;
    }

    private static void ConsumeSent(List<ArraySegment<byte>> sendList, int sent)
    {
        while (sent > 0 && sendList.Count > 0)
        {
            var head = sendList[0];
            if (sent >= head.Count)
            {
                sent -= head.Count;
                sendList.RemoveAt(0);
                continue;
            }

            sendList[0] = new ArraySegment<byte>(head.Array!, head.Offset + sent, head.Count - sent);
            sent = 0;
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
}
