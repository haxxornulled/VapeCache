using System.Collections.Concurrent;
using System.Net.Sockets;
using LanguageExt;
using LanguageExt.Common;
using Microsoft.Extensions.Logging.Abstractions;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.Connections;

public sealed class RedisPubSubServiceTests
{
    [Fact]
    public async Task PublishAsync_SendsPublishCommand_AndReturnsSubscriberCount()
    {
        var stream = new ScriptedResponseStream();
        stream.Enqueue(":3\r\n");
        var connection = new ScriptedConnection(stream);
        await using var factory = new QueueConnectionFactory(connection);
        var options = new TestOptionsMonitor<RedisPubSubOptions>(new RedisPubSubOptions());
        await using var sut = new RedisPubSubService(factory, options, NullLogger<RedisPubSubService>.Instance);

        var published = await sut.PublishAsync("orders", "hello"u8.ToArray(), CancellationToken.None);

        Assert.Equal(3, published);
        var sent = connection.TakeSentCommandUtf8();
        Assert.Contains("PUBLISH", sent, StringComparison.Ordinal);
        Assert.Contains("orders", sent, StringComparison.Ordinal);
        Assert.Contains("hello", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscribeAsync_DeliversChannelMessage()
    {
        var stream = new ScriptedResponseStream();
        stream.Enqueue("*3\r\n$9\r\nsubscribe\r\n$6\r\norders\r\n:1\r\n");
        stream.Enqueue("*3\r\n$7\r\nmessage\r\n$6\r\norders\r\n$5\r\nhello\r\n");

        var connection = new ScriptedConnection(stream);
        await using var factory = new QueueConnectionFactory(connection);
        var options = new TestOptionsMonitor<RedisPubSubOptions>(new RedisPubSubOptions());
        await using var sut = new RedisPubSubService(factory, options, NullLogger<RedisPubSubService>.Instance);

        var tcs = new TaskCompletionSource<RedisPubSubMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sub = await sut.SubscribeAsync(
            "orders",
            (message, _) =>
            {
                tcs.TrySetResult(message);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(tcs.Task, completed);
        var received = await tcs.Task;
        Assert.Equal("orders", received.Channel);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(received.Payload));
    }

    [Fact]
    public async Task SubscribeAsync_DropsNewestWhenQueueIsFull_AndDropOldestDisabled()
    {
        var stream = new ScriptedResponseStream();
        stream.Enqueue("*3\r\n$9\r\nsubscribe\r\n$6\r\norders\r\n:1\r\n");
        stream.Enqueue("*3\r\n$7\r\nmessage\r\n$6\r\norders\r\n$3\r\none\r\n");

        var connection = new ScriptedConnection(stream);
        await using var factory = new QueueConnectionFactory(connection);
        var options = new TestOptionsMonitor<RedisPubSubOptions>(new RedisPubSubOptions
        {
            DeliveryQueueCapacity = 1,
            DropOldestOnBackpressure = false
        });

        await using var sut = new RedisPubSubService(factory, options, NullLogger<RedisPubSubService>.Instance);
        var firstHandledGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handled = 0;

        await using var sub = await sut.SubscribeAsync(
            "orders",
            async (_, _) =>
            {
                var count = Interlocked.Increment(ref handled);
                if (count == 1)
                {
                    firstHandledGate.TrySetResult();
                    await releaseGate.Task.ConfigureAwait(false);
                }
            },
            CancellationToken.None);

        var reachedFirst = await Task.WhenAny(firstHandledGate.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(firstHandledGate.Task, reachedFirst);

        stream.Enqueue("*3\r\n$7\r\nmessage\r\n$6\r\norders\r\n$3\r\ntwo\r\n");
        stream.Enqueue("*3\r\n$7\r\nmessage\r\n$6\r\norders\r\n$5\r\nthree\r\n");
        await Task.Delay(150);
        releaseGate.TrySetResult();

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && Volatile.Read(ref handled) < 2)
            await Task.Delay(10);

        Assert.Equal(2, Volatile.Read(ref handled));
    }

    private sealed class QueueConnectionFactory : IRedisConnectionFactory
    {
        private readonly Queue<ScriptedConnection> _connections;
        private int _disposed;

        public QueueConnectionFactory(params ScriptedConnection[] connections)
        {
            _connections = new Queue<ScriptedConnection>(connections);
        }

        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) == 1)
                return ValueTask.FromResult<Result<IRedisConnection>>(new Result<IRedisConnection>(new ObjectDisposedException(nameof(QueueConnectionFactory))));

            lock (_connections)
            {
                if (_connections.Count == 0)
                    return ValueTask.FromResult<Result<IRedisConnection>>(new Result<IRedisConnection>(new InvalidOperationException("No scripted connections available.")));

                return ValueTask.FromResult<Result<IRedisConnection>>(_connections.Dequeue());
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return ValueTask.CompletedTask;

            lock (_connections)
            {
                _connections.Clear();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedConnection : IRedisConnection
    {
        private readonly ScriptedResponseStream _stream;
        private readonly ConcurrentQueue<byte[]> _sent = new();

        public ScriptedConnection(ScriptedResponseStream stream)
        {
            _stream = stream;
            Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }

        public Socket Socket { get; }
        public Stream Stream => _stream;

        public ValueTask<Result<Unit>> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
        {
            _sent.Enqueue(buffer.ToArray());
            return ValueTask.FromResult<Result<Unit>>(Prelude.unit);
        }

        public ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
            => ValueTask.FromResult<Result<int>>(0);

        public string TakeSentCommandUtf8()
        {
            Assert.True(_sent.TryDequeue(out var command));
            return System.Text.Encoding.UTF8.GetString(command);
        }

        public ValueTask DisposeAsync()
        {
            Socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedResponseStream : Stream
    {
        private readonly Queue<byte[]> _frames = new();
        private byte[]? _current;
        private int _offset;
        private readonly object _gate = new();

        public void Enqueue(string frame)
            => Enqueue(System.Text.Encoding.UTF8.GetBytes(frame));

        public void Enqueue(byte[] frame)
        {
            lock (_gate)
            {
                _frames.Enqueue(frame);
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                lock (_gate)
                {
                    if (_current is not null && _offset < _current.Length)
                    {
                        var take = Math.Min(buffer.Length, _current.Length - _offset);
                        _current.AsSpan(_offset, take).CopyTo(buffer.Span);
                        _offset += take;
                        return take;
                    }

                    if (_frames.Count > 0)
                    {
                        _current = _frames.Dequeue();
                        _offset = 0;
                        continue;
                    }
                }

                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.CompletedTask;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
