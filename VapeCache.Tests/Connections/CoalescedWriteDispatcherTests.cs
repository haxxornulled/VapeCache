using System.Net;
using System.Net.Sockets;
using System.Text;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Connections;

public sealed class CoalescedWriteDispatcherTests
{
    [Fact]
    public async Task SendAsync_high_concurrency_mixed_payloads_preserves_wire_framing()
    {
        Queue<PendingRequest> backlog = new();
        var enqueuedPendingOps = 0;
        var returnedHeaders = 0;
        var returnedPayloadArrays = 0;
        var aborted = 0;

        using var dispatcher = new CoalescedWriteDispatcher(
            coalescedWriteMaxBytes: 512 * 1024,
            coalescedWriteMaxSegments: 192,
            coalescedWriteSmallCopyThresholdBytes: 1536,
            enableAdaptiveCoalescing: true,
            adaptiveCoalescingLowDepth: 6,
            adaptiveCoalescingHighDepth: 56,
            adaptiveCoalescingMinWriteBytes: 64 * 1024,
            adaptiveCoalescingMinSegments: 48,
            adaptiveCoalescingMinSmallCopyThresholdBytes: 384,
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
            enqueuePendingOperation: (op, _) =>
            {
                Interlocked.Increment(ref enqueuedPendingOps);
                return ValueTask.CompletedTask;
            },
            nextPendingSequence: static () => 1,
            returnHeaderBuffer: _ => Interlocked.Increment(ref returnedHeaders),
            returnPayloadArray: _ => Interlocked.Increment(ref returnedPayloadArrays),
            abortPendingRequest: (_, _) => Interlocked.Increment(ref aborted));

        using var listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        using var client = new Socket(SocketType.Stream, ProtocolType.Tcp);
        client.NoDelay = true;
        var acceptTask = listener.AcceptAsync();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);
        using var server = await acceptTask;
        server.NoDelay = true;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        const int rounds = 120;
        const int requestsPerRound = 7;
        var expectedPendingOps = 0;
        var expectedHeaderReturns = 0;
        var expectedPayloadArrayReturns = 0;

        for (var round = 0; round < rounds; round++)
        {
            var requests = BuildRound(round, requestsPerRound);
            backlog = new Queue<PendingRequest>(requests.Skip(1));
            expectedPendingOps += requests.Count;
            expectedHeaderReturns += requests.Count;
            expectedPayloadArrayReturns += requests.Count(static r => r.PayloadArrayBuffer is not null);

            var expected = FlattenRequests(requests);
            var sendTask = dispatcher.SendAsync(requests[0], client, cts.Token);
            var received = await ReceiveExactAsync(server, expected.Length, cts.Token);
            await sendTask;

            Assert.Equal(expected, received);
            Assert.Empty(backlog);
        }

        Assert.Equal(expectedPendingOps, enqueuedPendingOps);
        Assert.Equal(expectedHeaderReturns, returnedHeaders);
        Assert.Equal(expectedPayloadArrayReturns, returnedPayloadArrays);
        Assert.Equal(0, aborted);
    }

    [Fact]
    public async Task SendAsync_when_pending_enqueue_fails_after_commit_keeps_already_sent_bytes_on_wire()
    {
        var returnedHeaders = 0;
        var returnedPayloadArrays = 0;
        var aborted = 0;

        using var dispatcher = new CoalescedWriteDispatcher(
            coalescedWriteMaxBytes: 64 * 1024,
            coalescedWriteMaxSegments: 64,
            coalescedWriteSmallCopyThresholdBytes: 1024,
            enableAdaptiveCoalescing: true,
            adaptiveCoalescingLowDepth: 4,
            adaptiveCoalescingHighDepth: 32,
            adaptiveCoalescingMinWriteBytes: 8 * 1024,
            adaptiveCoalescingMinSegments: 8,
            adaptiveCoalescingMinSmallCopyThresholdBytes: 256,
            crlfMemory: "\r\n"u8.ToArray(),
            tryDequeueWrite: (out PendingRequest req) =>
            {
                req = default;
                return false;
            },
            getWriteQueueDepth: () => 0,
            enqueuePendingOperation: static (_, _) => ValueTask.FromException(new InvalidOperationException("boom")),
            nextPendingSequence: static () => 1,
            returnHeaderBuffer: _ => Interlocked.Increment(ref returnedHeaders),
            returnPayloadArray: _ => Interlocked.Increment(ref returnedPayloadArrays),
            abortPendingRequest: (_, _) => Interlocked.Increment(ref aborted));

        using var listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        using var client = new Socket(SocketType.Stream, ProtocolType.Tcp);
        client.NoDelay = true;
        var acceptTask = listener.AcceptAsync();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);
        using var server = await acceptTask;
        server.NoDelay = true;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var request = CreateSAddRequest(42);
        var expected = FlattenRequests([request]);

        var sendTask = dispatcher.SendAsync(request, client, cts.Token);
        var received = await ReceiveExactAsync(server, expected.Length, cts.Token);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await sendTask);

        Assert.Equal("boom", ex.Message);
        Assert.Equal(expected, received);
        Assert.Equal(1, returnedHeaders);
        Assert.Equal(0, returnedPayloadArrays);
        Assert.Equal(1, aborted);
    }

    private static List<PendingRequest> BuildRound(int round, int count)
    {
        var requests = new List<PendingRequest>(count);
        for (var i = 0; i < count; i++)
        {
            var id = (round * count) + i;
            requests.Add(i % 3 == 0
                ? CreateRPushManyRequest(id)
                : CreateSAddRequest(id));
        }

        return requests;
    }

    private static PendingRequest CreateSAddRequest(int id)
    {
        var key = $"set:{id:D6}";
        var member = Encoding.UTF8.GetBytes($"user:{id:D6}");
        var len = RedisRespProtocol.GetSAddCommandLength(key, member.Length);
        var header = GC.AllocateUninitializedArray<byte>(len);
        var written = RedisRespProtocol.WriteSAddCommand(header.AsSpan(0, len), key, member);
        var op = CreatePendingOperation();
        return new PendingRequest(
            Command: header.AsMemory(0, written),
            Op: op,
            Payload: ReadOnlyMemory<byte>.Empty,
            Payloads: null,
            PayloadCount: 0,
            AppendCrlf: false,
            AppendCrlfPerPayload: true,
            HeaderBuffer: header,
            PayloadArrayBuffer: null);
    }

    private static PendingRequest CreateRPushManyRequest(int id)
    {
        var key = $"cart:{id:D6}";
        ReadOnlyMemory<byte>[] values =
        [
            Encoding.UTF8.GetBytes($"sku:{id:D6}"),
            Encoding.UTF8.GetBytes(new string('x', 24)),
            Encoding.UTF8.GetBytes($"qty:{(id % 5) + 1}")
        ];

        var commandPrefixLen = RedisRespProtocol.GetRPushManyPrefixLength(key, values.Length);
        var bulkPrefixTotalLen = 0;
        for (var i = 0; i < values.Length; i++)
            bulkPrefixTotalLen += RedisRespProtocol.GetBulkLengthPrefixLength(values[i].Length);

        var headerLen = commandPrefixLen + bulkPrefixTotalLen;
        var header = GC.AllocateUninitializedArray<byte>(headerLen);
        var written = RedisRespProtocol.WriteRPushManyPrefix(header.AsSpan(0, commandPrefixLen), key, values.Length);
        var payloads = new ReadOnlyMemory<byte>[values.Length * 3];

        var cursor = written;
        var idx = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var prefixLen = RedisRespProtocol.WriteBulkLength(header.AsSpan(cursor), values[i].Length);
            payloads[idx++] = header.AsMemory(cursor, prefixLen);
            payloads[idx++] = values[i];
            payloads[idx++] = "\r\n"u8.ToArray();
            cursor += prefixLen;
        }

        var op = CreatePendingOperation();
        return new PendingRequest(
            Command: header.AsMemory(0, written),
            Op: op,
            Payload: ReadOnlyMemory<byte>.Empty,
            Payloads: payloads,
            PayloadCount: idx,
            AppendCrlf: false,
            AppendCrlfPerPayload: false,
            HeaderBuffer: header,
            PayloadArrayBuffer: payloads);
    }

    private static PendingOperation CreatePendingOperation()
    {
        var op = new PendingOperation(CancellationToken.None, new SemaphoreSlim(1, 1), static _ => { }, null, null);
        op.Reset();
        op.Start(poolBulk: false, ct: CancellationToken.None, holdsSlot: false, sequenceId: 1);
        return op;
    }

    private static byte[] FlattenRequests(List<PendingRequest> requests)
    {
        var total = 0;
        for (var i = 0; i < requests.Count; i++)
        {
            var req = requests[i];
            total += req.Command.Length;
            if (!req.Payload.IsEmpty)
            {
                total += req.Payload.Length;
                if (req.AppendCrlf)
                    total += 2;
                continue;
            }

            if (req.PayloadCount <= 0 || req.Payloads is null)
                continue;

            for (var j = 0; j < req.PayloadCount; j++)
            {
                total += req.Payloads[j].Length;
                if (req.AppendCrlfPerPayload)
                    total += 2;
            }
        }

        var buffer = new byte[total];
        var offset = 0;
        for (var i = 0; i < requests.Count; i++)
        {
            var req = requests[i];
            req.Command.CopyTo(buffer.AsMemory(offset, req.Command.Length));
            offset += req.Command.Length;

            if (!req.Payload.IsEmpty)
            {
                req.Payload.CopyTo(buffer.AsMemory(offset, req.Payload.Length));
                offset += req.Payload.Length;
                if (req.AppendCrlf)
                {
                    buffer[offset++] = (byte)'\r';
                    buffer[offset++] = (byte)'\n';
                }
                continue;
            }

            if (req.PayloadCount <= 0 || req.Payloads is null)
                continue;

            for (var j = 0; j < req.PayloadCount; j++)
            {
                var seg = req.Payloads[j];
                seg.CopyTo(buffer.AsMemory(offset, seg.Length));
                offset += seg.Length;
                if (req.AppendCrlfPerPayload)
                {
                    buffer[offset++] = (byte)'\r';
                    buffer[offset++] = (byte)'\n';
                }
            }
        }

        return buffer;
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
}
