using System.Buffers;
using System.Collections.Generic;

namespace VapeCache.Infrastructure.Connections;

// Coalescing layer for scatter/gather RESP writes. Not yet wired into the multiplexer; intended for the upcoming SAEA transport.
internal readonly struct CoalescedPendingRequest
{
    public CoalescedPendingRequest(ReadOnlyMemory<byte>[] segments, int count, IDisposable? payloadOwner = null)
    {
        Segments = segments;
        Count = count;
        PayloadOwner = payloadOwner;
    }

    public ReadOnlyMemory<byte>[] Segments { get; }
    public int Count { get; }
    public IDisposable? PayloadOwner { get; }
}

internal sealed class CoalescedWriteBatch : IDisposable
{
    private const int DefaultScratchSize = 8 * 1024;

    public List<ReadOnlyMemory<byte>> SegmentsToWrite { get; } = new(32);
    public byte[]? Scratch { get; private set; }
    public int ScratchUsed { get; set; }
    public int ScratchBaseOffset { get; set; } // Tracks where in scratch the current write region starts
    public List<IDisposable> Owners { get; } = new(8);

    public void Reset()
    {
        SegmentsToWrite.Clear();
        Owners.Clear();
        ScratchUsed = 0;
        ScratchBaseOffset = 0;
    }

    public void EnsureScratch()
    {
        Scratch ??= ArrayPool<byte>.Shared.Rent(DefaultScratchSize);
    }

    public void Dispose()
    {
        if (Scratch is not null)
        {
            ArrayPool<byte>.Shared.Return(Scratch);
            Scratch = null;
            ScratchUsed = 0;
        }
        for (var i = 0; i < Owners.Count; i++)
            Owners[i].Dispose();
        Owners.Clear();
    }

    public void RecycleAfterSend()
    {
        for (var i = 0; i < Owners.Count; i++)
            Owners[i].Dispose();
        Owners.Clear();
        ScratchUsed = 0;
        ScratchBaseOffset = 0;
        SegmentsToWrite.Clear();
    }
}

internal sealed class Coalescer
{
    private const int MaxWriteBytes = 32 * 1024;
    private const int MaxSegments = 32;
    private const int SmallCopyThreshold = 512;

    private readonly Queue<CoalescedPendingRequest> _queue;

    public Coalescer(Queue<CoalescedPendingRequest> queue) => _queue = queue;

    public bool TryBuildBatch(CoalescedWriteBatch batch)
    {
        batch.Reset();
        var totalBytes = 0;

        while (_queue.Count > 0)
        {
            var req = _queue.Peek();
            var segments = req.Segments;

            // CRITICAL FIX: Calculate total size of this request BEFORE processing
            // to ensure we never split a single Redis command across batches
            var reqTotalBytes = 0;
            var reqSegmentCount = 0;
            for (var i = 0; i < req.Count; i++)
            {
                if (segments[i].Length > 0)
                {
                    reqTotalBytes += segments[i].Length;
                    reqSegmentCount++; // worst case: each segment becomes separate write
                }
            }

            // If adding this entire request would exceed limits AND we already have data,
            // commit current batch and process this request in next batch
            if (batch.SegmentsToWrite.Count > 0 || batch.ScratchUsed > 0)
            {
                if ((totalBytes + reqTotalBytes > MaxWriteBytes) ||
                    (batch.SegmentsToWrite.Count + reqSegmentCount > MaxSegments))
                {
                    CommitScratch(batch);
                    return true; // leave req in queue for next batch
                }
            }
            // If batch is empty, process this request anyway (even if it exceeds limits)
            // to ensure progress - we can't skip requests

            for (var i = 0; i < req.Count; i++)
            {
                var seg = segments[i];
                var segLen = seg.Length;

                if (segLen == 0) continue;

                if (segLen <= SmallCopyThreshold)
                {
                    batch.EnsureScratch();
                    if (batch.ScratchBaseOffset + batch.ScratchUsed + segLen > batch.Scratch!.Length)
                    {
                        CommitScratch(batch);
                        batch.EnsureScratch();
                    }

                    seg.Span.CopyTo(batch.Scratch.AsSpan(batch.ScratchBaseOffset + batch.ScratchUsed));
                    batch.ScratchUsed += segLen;
                    totalBytes += segLen;
                    continue;
                }

                CommitScratch(batch);
                batch.SegmentsToWrite.Add(seg);
                totalBytes += segLen;
            }

            if (req.PayloadOwner is not null)
                batch.Owners.Add(req.PayloadOwner);

            _queue.Dequeue();
        }

        CommitScratch(batch);
        return batch.SegmentsToWrite.Count > 0;
    }

    private static void CommitScratch(CoalescedWriteBatch batch)
    {
        if (batch.Scratch is not null && batch.ScratchUsed > 0)
        {
            // CRITICAL FIX: Use ScratchBaseOffset to avoid overwriting previously committed segments.
            // When we commit, we add a segment from [BaseOffset..BaseOffset+Used], then advance
            // BaseOffset so the next write doesn't overwrite this committed region.
            batch.SegmentsToWrite.Add(batch.Scratch.AsMemory(batch.ScratchBaseOffset, batch.ScratchUsed));
            batch.ScratchBaseOffset += batch.ScratchUsed;
            batch.ScratchUsed = 0;
        }
    }
}
