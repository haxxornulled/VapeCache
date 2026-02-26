using System.Collections.Generic;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Connections;

public sealed class CoalescerTests
{
    [Fact]
    public void TryBuildBatch_AdaptiveLowDepth_UsesMinimumLimits()
    {
        var queue = new Queue<CoalescedPendingRequest>();
        queue.Enqueue(CreateRequest(3 * 1024));
        queue.Enqueue(CreateRequest(3 * 1024));

        var coalescer = new Coalescer(
            queue,
            maxWriteBytes: 16 * 1024,
            maxSegments: 16,
            smallCopyThresholdBytes: 16,
            adaptiveEnabled: true,
            adaptiveLowDepth: 2,
            adaptiveHighDepth: 8,
            adaptiveMinWriteBytes: 4 * 1024,
            adaptiveMinSegments: 2,
            adaptiveMinSmallCopyThresholdBytes: 16);

        using var batch = new CoalescedWriteBatch();
        var built = coalescer.TryBuildBatch(batch);

        Assert.True(built);
        Assert.Single(batch.SegmentsToWrite);
        Assert.Single(queue);
    }

    [Fact]
    public void TryBuildBatch_AdaptiveHighDepth_UsesMaximumLimits()
    {
        var queue = new Queue<CoalescedPendingRequest>();
        for (var i = 0; i < 8; i++)
            queue.Enqueue(CreateRequest(1000));

        var coalescer = new Coalescer(
            queue,
            maxWriteBytes: 16 * 1024,
            maxSegments: 16,
            smallCopyThresholdBytes: 16,
            adaptiveEnabled: true,
            adaptiveLowDepth: 2,
            adaptiveHighDepth: 8,
            adaptiveMinWriteBytes: 4 * 1024,
            adaptiveMinSegments: 2,
            adaptiveMinSmallCopyThresholdBytes: 16);

        using var batch = new CoalescedWriteBatch();
        var built = coalescer.TryBuildBatch(batch);

        Assert.True(built);
        Assert.Equal(8, batch.SegmentsToWrite.Count);
        Assert.Empty(queue);
    }

    [Fact]
    public void TryBuildBatch_AdaptiveDisabled_AlwaysUsesMaximumLimits()
    {
        var queue = new Queue<CoalescedPendingRequest>();
        queue.Enqueue(CreateRequest(1000));
        queue.Enqueue(CreateRequest(1000));

        var coalescer = new Coalescer(
            queue,
            maxWriteBytes: 16 * 1024,
            maxSegments: 16,
            smallCopyThresholdBytes: 16,
            adaptiveEnabled: false,
            adaptiveLowDepth: 2,
            adaptiveHighDepth: 8,
            adaptiveMinWriteBytes: 4 * 1024,
            adaptiveMinSegments: 2,
            adaptiveMinSmallCopyThresholdBytes: 16);

        using var batch = new CoalescedWriteBatch();
        var built = coalescer.TryBuildBatch(batch);

        Assert.True(built);
        Assert.Equal(2, batch.SegmentsToWrite.Count);
        Assert.Empty(queue);
    }

    private static CoalescedPendingRequest CreateRequest(int bytes)
    {
        var payload = new byte[bytes];
        payload[0] = (byte)'*';
        ReadOnlyMemory<byte>[] segments = [payload];
        return new CoalescedPendingRequest(segments, count: 1);
    }
}
