using System;
using System.Buffers;
using System.Threading.Tasks;
using VapeCache.Infrastructure.Connections;
using Xunit;

namespace VapeCache.PerfGates.Tests;

public class PerfGatesZeroAllocTests
{
    [Fact]
    public void RespParserLite_ZeroAlloc_CommonFrames()
    {
        var frames = new[]
        {
            "+OK\r\n"u8.ToArray(),
            ":123\r\n"u8.ToArray(),
            "-ERR msg\r\n"u8.ToArray(),
            "$3\r\nfoo\r\n"u8.ToArray(),
            "*2\r\n$3\r\nGET\r\n$3\r\nkey\r\n"u8.ToArray()
        };

        // Warmup
        foreach (var frame in frames)
        {
            var ok = RespParserLite.TryParse(frame, out var consumedWarm, out _);
            Assert.True(ok);
            Assert.Equal(frame.Length, consumedWarm);
        }

        const int iterations = 100_000;
        var baseline = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            foreach (var frame in frames)
            {
                var ok = RespParserLite.TryParse(frame, out var consumed, out _);
                if (!ok || consumed != frame.Length)
                    throw new InvalidOperationException("RESP parse failed in zero-allocation gate.");
            }
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - baseline;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task SocketAwaitableEventArgs_SimulatedCompletion_ZeroAlloc()
    {
        var args = new SocketIoAwaitableEventArgs();
        var buffers = new[] { new ArraySegment<byte>(Array.Empty<byte>()) };

        args.Reset();
        args.SetBuffer(buffers, 1);
        args.CompleteForTests(0);

        for (var i = 0; i < 1000; i++)
        {
            args.Reset();
            args.SetBuffer(buffers, 1);
            var warmVt = args.WaitAsync();
            args.CompleteForTests(0);
            await warmVt;
        }

        const int iterations = 100_000;
        var baseline = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            args.Reset();
            args.SetBuffer(buffers, 1);
            var vt = args.WaitAsync();
            args.CompleteForTests(0);
            await vt;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - baseline;
        Assert.True(allocated <= 32, $"SAEA send awaitable allocated {allocated} bytes.");
    }

    [Fact]
    public void RedisMetrics_WithTracingDisabled_DoesNotAllocatePerOperation()
    {
        for (var i = 0; i < 1_000; i++)
        {
            RedisMetrics.CommandCalls.Add(1);
            RedisMetrics.CommandFailures.Add(1);
            RedisMetrics.CommandMs.Record(0.25);
            _ = RedisTracing.StartCommand("GET", instrumentationEnabled: false);
        }

        const int iterations = 200_000;
        var baseline = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            RedisMetrics.CommandCalls.Add(1);
            RedisMetrics.CommandFailures.Add(1);
            RedisMetrics.CommandMs.Record(0.25);
            _ = RedisTracing.StartCommand("GET", instrumentationEnabled: false);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - baseline;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task AwaitableSocketArgs_SimulatedCompletion_ZeroAlloc()
    {
        var args = new SocketIoAwaitableEventArgs();

        var initVt = args.BeginForTests();
        args.CompleteForTests(0);
        await initVt;

        for (var i = 0; i < 1000; i++)
        {
            var warmVt = args.BeginForTests();
            args.CompleteForTests(0);
            await warmVt;
        }

        const int iterations = 100_000;
        var baseline = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            var vt = args.BeginForTests();
            args.CompleteForTests(0);
            await vt;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - baseline;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task SocketAwaitableEventArgs_SyncError_Completes()
    {
        var args = new SocketIoAwaitableEventArgs();
        args.Reset();
        args.SetBuffer(new[] { new ArraySegment<byte>(Array.Empty<byte>()) }, 1);
        var vt = args.WaitAsync();
        args.CompleteForTests(0, System.Net.Sockets.SocketError.ConnectionReset);
        await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(async () => await vt);
    }

    [Fact]
    public async Task AwaitableSocketArgs_SyncError_Completes()
    {
        var args = new SocketIoAwaitableEventArgs();
        var vt = args.BeginForTests();
        args.CompleteForTests(0, System.Net.Sockets.SocketError.ConnectionReset);
        await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(async () => await vt);
    }

    [Fact]
    public async Task SocketAwaitableEventArgs_DoubleCompletion_Idempotent()
    {
        var args = new SocketIoAwaitableEventArgs();
        args.Reset();
        args.SetBuffer(new[] { new ArraySegment<byte>(Array.Empty<byte>()) }, 1);
        var vt = args.WaitAsync();
        args.CompleteForTests(1);
        args.CompleteForTests(1); // should be ignored
        Assert.Equal(1, await vt);
    }

    [Fact]
    public async Task AwaitableSocketArgs_DoubleCompletion_Idempotent()
    {
        var args = new SocketIoAwaitableEventArgs();
        var vt = args.BeginForTests();
        args.CompleteForTests(2);
        args.CompleteForTests(2); // ignored
        Assert.Equal(2, await vt);
    }

    // Removed: PendingOperationForTests no longer exists
    // The internal PendingOperation class is tested through integration tests
}
