using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using VapeCache.Infrastructure.Connections;
using Xunit;

namespace VapeCache.PerfGates.Tests;

public sealed class CoalescedWritePerfGateTests
{
    [Fact]
    public async Task BurstyDispatch_P95P99_StaysWithinBudget()
    {
        Queue<PendingRequest> backlog = new();
        var aborted = 0;

        using var dispatcher = new CoalescedWriteDispatcher(
            coalescedWriteMaxBytes: 256 * 1024,
            coalescedWriteMaxSegments: 96,
            coalescedWriteSmallCopyThresholdBytes: 1024,
            enableAdaptiveCoalescing: true,
            adaptiveCoalescingLowDepth: 4,
            adaptiveCoalescingHighDepth: 32,
            adaptiveCoalescingMinWriteBytes: 32 * 1024,
            adaptiveCoalescingMinSegments: 24,
            adaptiveCoalescingMinSmallCopyThresholdBytes: 256,
            crlfMemory: "\r\n"u8.ToArray(),
            tryDequeueWrite: (out PendingRequest req) =>
            {
                if (backlog.Count > 0)
                {
                    req = backlog.Dequeue();
                    return true;
                }

                req = default;
                return false;
            },
            getWriteQueueDepth: () => backlog.Count,
            enqueuePendingOperation: static (_, _) => ValueTask.CompletedTask,
            nextPendingSequence: static () => 1,
            returnHeaderBuffer: static _ => { },
            returnPayloadArray: static _ => { },
            abortPendingRequest: (_, _) => Interlocked.Increment(ref aborted),
            coalescingEnterQueueDepth: 6,
            coalescingExitQueueDepth: 2,
            coalescedWriteMaxOperations: 32,
            coalescingSpinBudget: 6);

        using var listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        using var client = new Socket(SocketType.Stream, ProtocolType.Tcp);
        client.NoDelay = true;
        var acceptTask = listener.AcceptAsync();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);
        using var server = await acceptTask;
        server.NoDelay = true;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var latenciesMs = new List<double>(160);

        const int rounds = 160;
        for (var round = 0; round < rounds; round++)
        {
            var burst = round % 8 == 0;
            var requestCount = burst ? 7 : 1;
            var requests = CreateRequests(requestCount);
            backlog = requestCount > 1
                ? new Queue<PendingRequest>(requests.Skip(1))
                : new Queue<PendingRequest>();

            var expectedWireBytes = requestCount * requests[0].Command.Length;
            var start = Stopwatch.GetTimestamp();
            var sendTask = dispatcher.SendAsync(requests[0], client, generation: 1, cts.Token);
            _ = await ReceiveExactAsync(server, expectedWireBytes, cts.Token);
            await sendTask;
            var elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            latenciesMs.Add(elapsedMs);
        }

        Assert.Equal(0, aborted);
        var p95 = Percentile(latenciesMs, 0.95);
        var p99 = Percentile(latenciesMs, 0.99);
        Assert.True(p95 <= 25.0, $"Bursty coalesced write p95 too high: {p95:F3}ms");
        Assert.True(p99 <= 40.0, $"Bursty coalesced write p99 too high: {p99:F3}ms");
    }

    private static List<PendingRequest> CreateRequests(int count)
    {
        var requests = new List<PendingRequest>(count);
        for (var i = 0; i < count; i++)
        {
            var command = "*1\r\n$4\r\nPING\r\n"u8.ToArray();
            var op = CreatePendingOperation();
            requests.Add(new PendingRequest(
                Command: command,
                Op: op,
                Payload: ReadOnlyMemory<byte>.Empty,
                Payloads: null,
                PayloadCount: 0,
                AppendCrlf: false,
                AppendCrlfPerPayload: true,
                HeaderBuffer: command,
                PayloadArrayBuffer: null));
        }

        return requests;
    }

    private static PendingOperation CreatePendingOperation()
    {
        var op = new PendingOperation(CancellationToken.None, new SemaphoreSlim(1, 1), static _ => { }, null, null);
        op.Reset();
        op.Start(poolBulk: false, ct: CancellationToken.None, holdsSlot: false, sequenceId: 1);
        return op;
    }

    private static async Task<byte[]> ReceiveExactAsync(Socket socket, int expectedLength, CancellationToken ct)
    {
        var received = new byte[expectedLength];
        var offset = 0;
        while (offset < expectedLength)
        {
            var read = await socket.ReceiveAsync(received.AsMemory(offset, expectedLength - offset), SocketFlags.None, ct);
            if (read <= 0)
                throw new InvalidOperationException($"Socket closed before expected payload was fully received ({offset}/{expectedLength}).");

            offset += read;
        }

        return received;
    }

    private static double Percentile(List<double> samples, double percentile)
    {
        if (samples.Count == 0)
            return 0;

        var ordered = samples.ToArray();
        Array.Sort(ordered);
        var rank = (int)Math.Ceiling(percentile * ordered.Length) - 1;
        rank = Math.Clamp(rank, 0, ordered.Length - 1);
        return ordered[rank];
    }
}
