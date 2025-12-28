using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LanguageExt;
using LanguageExt.Common;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;
using Xunit;

namespace VapeCache.Tests.Connections;

public class RingQueueTests
{
    [Fact]
    public async Task MpscRingQueue_SynchronousWait_Enqueues()
    {
        var queue = CreateQueue("MpscRingQueue`1", capacity: 4);

        await InvokeEnqueueNoSpinAsync(queue, 42, CancellationToken.None);
        var value = await InvokeDequeueAsync(queue, CancellationToken.None);

        Assert.Equal(42, value);
    }

    [Fact]
    public async Task SpscRingQueue_SynchronousWait_Enqueues()
    {
        var queue = CreateQueue("SpscRingQueue`1", capacity: 4);

        await InvokeEnqueueNoSpinAsync(queue, 7, CancellationToken.None);
        var value = await InvokeDequeueAsync(queue, CancellationToken.None);

        Assert.Equal(7, value);
    }

    [Fact]
    public async Task MpscRingQueue_ConcurrentEnqueueDequeue_NoLoss()
    {
        var queue = CreateQueue("MpscRingQueue`1", capacity: 8);
        const int producers = 4;
        const int perProducer = 200;
        var expected = producers * perProducer;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var consumer = Task.Run(async () =>
        {
            var seen = 0;
            for (var i = 0; i < expected; i++)
            {
                _ = await InvokeDequeueAsync(queue, cts.Token);
                seen++;
            }
            return seen;
        }, cts.Token);

        var producerTasks = new List<Task>();
        for (var p = 0; p < producers; p++)
        {
            producerTasks.Add(Task.Run(async () =>
            {
                for (var i = 0; i < perProducer; i++)
                {
                    await InvokeEnqueueAsync(queue, i, cts.Token);
                    if ((i & 0x1F) == 0)
                        await Task.Yield();
                }
            }, cts.Token));
        }

        await Task.WhenAll(producerTasks);
        var consumed = await consumer;

        Assert.Equal(expected, consumed);
    }

    [Fact]
    public async Task CapacityRoundedUpToPowerOfTwo()
    {
        await using var mux = new RedisMultiplexedConnection(new NoopFactory(), maxInFlight: 75, coalesceWrites: false);

        var writesField = typeof(RedisMultiplexedConnection).GetField("_writes", BindingFlags.Instance | BindingFlags.NonPublic)
                         ?? throw new InvalidOperationException("Writes queue not found.");
        var writes = writesField.GetValue(mux) ?? throw new InvalidOperationException("Writes queue missing.");
        var capacityProp = writes.GetType().GetProperty("Capacity") ?? throw new InvalidOperationException("Capacity property missing.");
        var capacity = (int)capacityProp.GetValue(writes)!;

        Assert.Equal(512, capacity); // 75*4=300 => round up to 512
    }

    [Fact]
    public async Task Dispose_CompletesPendingOperations()
    {
        await using var mux = new RedisMultiplexedConnection(new BlockingFactory(), maxInFlight: 4, coalesceWrites: false);

        var pending = mux.ExecuteAsync(RedisRespProtocol.PingCommand, CancellationToken.None);
        await Task.Delay(50);

        await mux.DisposeAsync();

        var pendingTask = pending.AsTask();
        var completed = await Task.WhenAny(pendingTask, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.True(completed == pendingTask, "Pending operation did not complete within 2 seconds");

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await pendingTask);
        Assert.True(ex is ObjectDisposedException || ex is OperationCanceledException, $"Unexpected exception type: {ex.GetType()}");
    }

    private static object CreateQueue(string name, int capacity)
    {
        var type = typeof(RedisMultiplexedConnection).GetNestedType(name, BindingFlags.NonPublic);
        if (type is null) throw new InvalidOperationException($"Type {name} not found.");
        var concrete = type.MakeGenericType(typeof(int));
        return Activator.CreateInstance(concrete, capacity)!;
    }

    private static async ValueTask InvokeEnqueueAsync(object queue, int value, CancellationToken ct)
    {
        var method = queue.GetType().GetMethod("EnqueueAsync");
        var vt = (ValueTask)method!.Invoke(queue, new object[] { value, ct })!;
        await vt.ConfigureAwait(false);
    }

    private static async ValueTask InvokeEnqueueNoSpinAsync(object queue, int value, CancellationToken ct)
    {
        var method = queue.GetType().GetMethod("EnqueueAsyncNoSpinForTests", BindingFlags.Instance | BindingFlags.NonPublic);
        var vt = (ValueTask)method!.Invoke(queue, new object[] { value, ct })!;
        await vt.ConfigureAwait(false);
    }

    private static async ValueTask<int> InvokeDequeueAsync(object queue, CancellationToken ct)
    {
        var method = queue.GetType().GetMethod("DequeueAsync");
        var vt = (ValueTask<int>)method!.Invoke(queue, new object[] { ct })!;
        return await vt.ConfigureAwait(false);
    }

    private sealed class NoopFactory : IRedisConnectionFactory
    {
        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct) =>
            ValueTask.FromResult(new Result<IRedisConnection>(new NoopConn()));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class NoopConn : IRedisConnection
        {
            public Socket Socket => throw new NotSupportedException();
            public Stream Stream => Stream.Null;
            public ValueTask<Result<Unit>> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct) =>
                ValueTask.FromResult(new Result<Unit>(Prelude.unit));
            public ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct) =>
                ValueTask.FromResult(new Result<int>(0));
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingFactory : IRedisConnectionFactory
    {
        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct) =>
            ValueTask.FromResult(new Result<IRedisConnection>(new BlockingConn()));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingConn : IRedisConnection
    {
        private readonly BlockingStream _stream = new();

        public Socket Socket => throw new NotSupportedException();
        public Stream Stream => _stream;

        public ValueTask<Result<Unit>> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct) =>
            ValueTask.FromResult(new Result<Unit>(Prelude.unit));

        public ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct) =>
            ValueTask.FromResult(new Result<int>(0));

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingStream : Stream
    {
        private readonly TaskCompletionSource<int> _blockTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var ctr = cancellationToken.Register(() => _blockTcs.TrySetCanceled(cancellationToken));
            try
            {
                return await _blockTcs.Task.ConfigureAwait(false);
            }
            finally
            {
                ctr.Dispose();
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _blockTcs.TrySetException(new ObjectDisposedException(nameof(BlockingStream)));
            base.Dispose(disposing);
        }
    }
}
