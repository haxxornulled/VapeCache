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
                RespParserLite.TryParse(frame, out _, out _);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - baseline;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void SocketAwaitableEventArgs_SimulatedCompletion_ZeroAlloc()
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
            warmVt.GetAwaiter().GetResult();
        }

        const int iterations = 100_000;
        var baseline = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            args.Reset();
            args.SetBuffer(buffers, 1);
            var vt = args.WaitAsync();
            args.CompleteForTests(0);
            vt.GetAwaiter().GetResult();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - baseline;
        Assert.True(allocated <= 32, $"SAEA send awaitable allocated {allocated} bytes.");
    }

    [Fact]
    public void AwaitableSocketArgs_SimulatedCompletion_ZeroAlloc()
    {
        var args = new SocketIoAwaitableEventArgs();

        args.BeginForTests();
        args.CompleteForTests(0);

        for (var i = 0; i < 1000; i++)
        {
            var warmVt = args.BeginForTests();
            args.CompleteForTests(0);
            warmVt.GetAwaiter().GetResult();
        }

        const int iterations = 100_000;
        var baseline = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            var vt = args.BeginForTests();
            args.CompleteForTests(0);
            vt.GetAwaiter().GetResult();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - baseline;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void SocketAwaitableEventArgs_SyncError_Completes()
    {
        var args = new SocketIoAwaitableEventArgs();
        args.Reset();
        args.SetBuffer(new[] { new ArraySegment<byte>(Array.Empty<byte>()) }, 1);
        var vt = args.WaitAsync();
        args.CompleteForTests(0, System.Net.Sockets.SocketError.ConnectionReset);
        Assert.Throws<System.Net.Sockets.SocketException>(() => vt.GetAwaiter().GetResult());
    }

    [Fact]
    public void AwaitableSocketArgs_SyncError_Completes()
    {
        var args = new SocketIoAwaitableEventArgs();
        var vt = args.BeginForTests();
        args.CompleteForTests(0, System.Net.Sockets.SocketError.ConnectionReset);
        Assert.Throws<System.Net.Sockets.SocketException>(() => vt.GetAwaiter().GetResult());
    }

    [Fact]
    public void SocketAwaitableEventArgs_DoubleCompletion_Idempotent()
    {
        var args = new SocketIoAwaitableEventArgs();
        args.Reset();
        args.SetBuffer(new[] { new ArraySegment<byte>(Array.Empty<byte>()) }, 1);
        var vt = args.WaitAsync();
        args.CompleteForTests(1);
        args.CompleteForTests(1); // should be ignored
        Assert.Equal(1, vt.GetAwaiter().GetResult());
    }

    [Fact]
    public void AwaitableSocketArgs_DoubleCompletion_Idempotent()
    {
        var args = new SocketIoAwaitableEventArgs();
        var vt = args.BeginForTests();
        args.CompleteForTests(2);
        args.CompleteForTests(2); // ignored
        Assert.Equal(2, vt.GetAwaiter().GetResult());
    }

    [Fact]
    public void PendingOperationForTests_RejectsDoubleResult()
    {
        var op = new RedisMultiplexedConnection.PendingOperationForTests();
        op.Start();
        op.SetResult(1);
        Assert.Throws<InvalidOperationException>(() => op.SetResult(2));
    }
}
