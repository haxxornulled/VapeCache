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
    public List<IDisposable> Owners { get; } = new(8);

    public void Reset()
    {
        SegmentsToWrite.Clear();
        Owners.Clear();
        ScratchUsed = 0;
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
            for (var i = 0; i < req.Count; i++)
            {
                var seg = segments[i];
                var segLen = seg.Length;

                if ((totalBytes + segLen > MaxWriteBytes) || (batch.SegmentsToWrite.Count + 1 > MaxSegments))
                {
                    CommitScratch(batch);
                    if (batch.SegmentsToWrite.Count > 0)
                        return true; // leave req in queue for next batch
                }

                if (segLen == 0) continue;

                if (segLen <= SmallCopyThreshold)
                {
                    batch.EnsureScratch();
                    if (batch.ScratchUsed + segLen > batch.Scratch!.Length)
                    {
                        CommitScratch(batch);
                        batch.EnsureScratch();
                    }

                    seg.Span.CopyTo(batch.Scratch.AsSpan(batch.ScratchUsed));
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
            batch.SegmentsToWrite.Add(batch.Scratch.AsMemory(0, batch.ScratchUsed));
            batch.ScratchUsed = 0;
        }
    }
}
