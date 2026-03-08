using System.Buffers;
using System.Collections.Generic;
using System.Threading;

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
    public byte[]? Scratch { get; internal set; }
    public int ScratchUsed { get; set; }
    public int ScratchBaseOffset { get; set; }
    public List<IDisposable> Owners { get; } = new(8);

    /// <summary>
    /// Executes value.
    /// </summary>
    public void Reset()
    {
        SegmentsToWrite.Clear();
        Owners.Clear();
        ScratchUsed = 0;
        ScratchBaseOffset = 0;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public void EnsureScratch()
    {
        Scratch ??= ArrayPool<byte>.Shared.Rent(DefaultScratchSize);
    }

    /// <summary>
    /// Releases resources used by the current instance.
    /// </summary>
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

    /// <summary>
    /// Executes value.
    /// </summary>
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
    private readonly int _maxWriteBytes;
    private readonly int _maxSegments;
    private readonly int _smallCopyThreshold;
    private readonly bool _adaptiveEnabled;
    private readonly int _adaptiveLowDepth;
    private readonly int _adaptiveHighDepth;
    private readonly int _adaptiveMinWriteBytes;
    private readonly int _adaptiveMinSegments;
    private readonly int _adaptiveMinSmallCopyThreshold;
    private readonly int _maxOperations;

    private readonly Queue<CoalescedPendingRequest> _queue;
    private readonly Func<int>? _queueDepthProvider;

    public Coalescer(
        Queue<CoalescedPendingRequest> queue,
        int maxWriteBytes = 1024 * 1024,
        int maxSegments = 256,
        int smallCopyThresholdBytes = 2048,
        bool adaptiveEnabled = true,
        int adaptiveLowDepth = 4,
        int adaptiveHighDepth = 64,
        int adaptiveMinWriteBytes = 64 * 1024,
        int adaptiveMinSegments = 64,
        int adaptiveMinSmallCopyThresholdBytes = 512,
        int maxOperations = 128,
        Func<int>? queueDepthProvider = null)
    {
        ArgumentNullException.ThrowIfNull(queue);

        _queue = queue;
        _maxWriteBytes = Math.Clamp(maxWriteBytes, 4 * 1024, 1024 * 1024);
        _maxSegments = Math.Clamp(maxSegments, 4, 256);
        _smallCopyThreshold = Math.Clamp(smallCopyThresholdBytes, 64, 8 * 1024);
        _adaptiveEnabled = adaptiveEnabled;
        _adaptiveLowDepth = Math.Max(1, adaptiveLowDepth);
        _adaptiveHighDepth = Math.Max(_adaptiveLowDepth + 1, adaptiveHighDepth);
        _adaptiveMinWriteBytes = Math.Clamp(adaptiveMinWriteBytes, 4 * 1024, _maxWriteBytes);
        _adaptiveMinSegments = Math.Clamp(adaptiveMinSegments, 4, _maxSegments);
        _adaptiveMinSmallCopyThreshold = Math.Clamp(adaptiveMinSmallCopyThresholdBytes, 64, _smallCopyThreshold);
        _maxOperations = Math.Clamp(maxOperations, 1, 2048);
        _queueDepthProvider = queueDepthProvider;
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryBuildBatch(CoalescedWriteBatch batch)
    {
        batch.Reset();
        var totalBytes = 0;
        var operationsInBatch = 0;
        var (maxWriteBytes, maxSegments, smallCopyThreshold) = GetEffectiveLimits();

        while (_queue.Count > 0)
        {
            if (operationsInBatch >= _maxOperations && (batch.SegmentsToWrite.Count > 0 || batch.ScratchUsed > 0))
            {
                CommitScratch(batch, smallCopyThreshold);
                return true;
            }

            var req = _queue.Peek();
            var segments = req.Segments;

            // Calculate request size to avoid splitting Redis commands across batches
            var reqTotalBytes = 0;
            var reqSegmentCount = 0;
            for (var i = 0; i < req.Count; i++)
            {
                if (segments[i].Length > 0)
                {
                    reqTotalBytes += segments[i].Length;
                    reqSegmentCount++;
                }
            }

            // If adding this request would exceed limits and we have data, commit current batch
            if (batch.SegmentsToWrite.Count > 0 || batch.ScratchUsed > 0)
            {
                if ((totalBytes + reqTotalBytes > maxWriteBytes) ||
                    (batch.SegmentsToWrite.Count + reqSegmentCount > maxSegments))
                {
                    CommitScratch(batch, smallCopyThreshold);
                    return true;
                }
            }

            for (var i = 0; i < req.Count; i++)
            {
                var seg = segments[i];
                var segLen = seg.Length;

                if (segLen == 0) continue;

                if (segLen <= smallCopyThreshold)
                {
                    batch.EnsureScratch();
                    var requiredSpace = batch.ScratchBaseOffset + batch.ScratchUsed + segLen;
                    if (requiredSpace > batch.Scratch!.Length)
                    {
                        CommitScratch(batch, smallCopyThreshold);
                        batch.EnsureScratch();
                    }

                    seg.Span.CopyTo(batch.Scratch.AsSpan(batch.ScratchBaseOffset + batch.ScratchUsed));
                    batch.ScratchUsed += segLen;
                    totalBytes += segLen;
                    continue;
                }

                CommitScratch(batch, smallCopyThreshold);
                batch.SegmentsToWrite.Add(seg);
                totalBytes += segLen;
            }

            if (req.PayloadOwner is not null)
                batch.Owners.Add(req.PayloadOwner);

            _queue.Dequeue();
            operationsInBatch++;
        }

        CommitScratch(batch, smallCopyThreshold);
        return batch.SegmentsToWrite.Count > 0;
    }

    private (int MaxWriteBytes, int MaxSegments, int SmallCopyThreshold) GetEffectiveLimits()
    {
        if (!_adaptiveEnabled)
            return (_maxWriteBytes, _maxSegments, _smallCopyThreshold);

        var depth = Math.Max(_queue.Count, _queueDepthProvider?.Invoke() ?? 0);
        if (depth <= _adaptiveLowDepth)
            return (_adaptiveMinWriteBytes, _adaptiveMinSegments, _adaptiveMinSmallCopyThreshold);
        
        // Tail-latency protection: when queue depth is high, avoid "max-sized" coalesced writes
        // that can create head-of-line blocking for commands behind the current batch.
        if (depth >= _adaptiveHighDepth * 2)
        {
            var cappedBytes = Math.Min(_adaptiveMinWriteBytes, 96 * 1024);
            var cappedSegments = Math.Min(_adaptiveMinSegments, 24);
            var cappedCopyThreshold = Math.Min(_adaptiveMinSmallCopyThreshold, 384);
            return (cappedBytes, cappedSegments, cappedCopyThreshold);
        }

        if (depth >= _adaptiveHighDepth)
        {
            var cappedBytes = Math.Min(_maxWriteBytes, Math.Max(_adaptiveMinWriteBytes, 256 * 1024));
            var cappedSegments = Math.Min(_maxSegments, Math.Max(_adaptiveMinSegments, 64));
            var cappedCopyThreshold = Math.Min(_smallCopyThreshold, Math.Max(_adaptiveMinSmallCopyThreshold, 1024));
            return (cappedBytes, cappedSegments, cappedCopyThreshold);
        }

        var span = _adaptiveHighDepth - _adaptiveLowDepth;
        var numerator = depth - _adaptiveLowDepth;
        var ratio = (double)numerator / span;

        return (
            Lerp(_adaptiveMinWriteBytes, _maxWriteBytes, ratio),
            Lerp(_adaptiveMinSegments, _maxSegments, ratio),
            Lerp(_adaptiveMinSmallCopyThreshold, _smallCopyThreshold, ratio));
    }

    private static int Lerp(int min, int max, double ratio)
    {
        var value = min + ((max - min) * ratio);
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private static void CommitScratch(CoalescedWriteBatch batch, int smallCopyThreshold)
    {
        if (batch.Scratch is not null && batch.ScratchUsed > 0)
        {
            batch.SegmentsToWrite.Add(batch.Scratch.AsMemory(batch.ScratchBaseOffset, batch.ScratchUsed));

            var nextOffset = batch.ScratchBaseOffset + batch.ScratchUsed;
            var remainingSpace = batch.Scratch.Length - nextOffset;

            if (remainingSpace < smallCopyThreshold + 1024)
            {
                // Keep the current scratch alive until the batch is sent.
                batch.Owners.Add(new ArrayPoolLease(batch.Scratch));
                batch.Scratch = ArrayPool<byte>.Shared.Rent(8192);
                batch.ScratchBaseOffset = 0;
            }
            else
            {
                batch.ScratchBaseOffset = nextOffset;
            }
            batch.ScratchUsed = 0;
        }
    }
}

internal sealed class ArrayPoolLease : IDisposable
{
    private byte[]? _buffer;

    public ArrayPoolLease(byte[] buffer) => _buffer = buffer;

    /// <summary>
    /// Releases resources used by the current instance.
    /// </summary>
    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
            ArrayPool<byte>.Shared.Return(buffer);
    }
}
