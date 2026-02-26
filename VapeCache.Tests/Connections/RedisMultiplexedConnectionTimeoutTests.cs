using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LanguageExt;
using LanguageExt.Common;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;
using Xunit;

namespace VapeCache.Tests.Connections;

public class RedisMultiplexedConnectionTimeoutTests
{
    [Fact]
    public async Task ExecuteAsync_CoalescedSendFailure_CompletesAllDrainedOperations()
    {
        var mux = new RedisMultiplexedConnection(
            new DisposedSocketFactory(),
            maxInFlight: 8,
            coalesceWrites: true,
            responseTimeout: TimeSpan.FromSeconds(1));

        try
        {
            var first = mux.ExecuteAsync(RedisRespProtocol.PingCommand, CancellationToken.None).AsTask();
            var second = mux.ExecuteAsync(RedisRespProtocol.PingCommand, CancellationToken.None).AsTask();

            var all = Task.WhenAll(
                first.ContinueWith(static _ => { }, TaskScheduler.Default),
                second.ContinueWith(static _ => { }, TaskScheduler.Default));
            var completed = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(all, completed);

            await Assert.ThrowsAnyAsync<Exception>(async () => await first);
            await Assert.ThrowsAnyAsync<Exception>(async () => await second);
        }
        finally
        {
            await mux.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecuteAsync_ResponseTimeout_FailsCommand()
    {
        var mux = new RedisMultiplexedConnection(
            new NeverRespondingFactory(),
            maxInFlight: 1,
            coalesceWrites: false,
            responseTimeout: TimeSpan.FromMilliseconds(50));

        try
        {
            var task = mux.ExecuteAsync(RedisRespProtocol.PingCommand, CancellationToken.None).AsTask();
            var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(task, completed);
            await Assert.ThrowsAsync<TimeoutException>(() => task);
        }
        finally
        {
            await mux.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecuteAsync_FatalSocketError_CompletesWithException()
    {
        var mux = new RedisMultiplexedConnection(
            new ThrowingFactory(),
            maxInFlight: 1,
            coalesceWrites: false,
            responseTimeout: TimeSpan.FromSeconds(1));

        try
        {
            var task = mux.ExecuteAsync(RedisRespProtocol.PingCommand, CancellationToken.None).AsTask();
            var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(task, completed);
            await Assert.ThrowsAsync<IOException>(() => task);
        }
        finally
        {
            await mux.DisposeAsync();
        }
    }

    private sealed class NeverRespondingFactory : IRedisConnectionFactory
    {
        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
            => ValueTask.FromResult(new Result<IRedisConnection>(new FakeConn(new NeverRespondingStream())));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingFactory : IRedisConnectionFactory
    {
        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
            => ValueTask.FromResult(new Result<IRedisConnection>(new FakeConn(new ThrowingReadStream())));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DisposedSocketFactory : IRedisConnectionFactory
    {
        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
            => ValueTask.FromResult(new Result<IRedisConnection>(new DisposedSocketConn()));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeConn : IRedisConnection
    {
        private readonly Stream _stream;

        public FakeConn(Stream stream)
        {
            _stream = stream;
        }

        public Socket Socket => throw new NotSupportedException();
        public Stream Stream => _stream;

        public ValueTask<Result<Unit>> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
            => ValueTask.FromResult<Result<Unit>>(Prelude.unit);

        public ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
            => ValueTask.FromResult<Result<int>>(0);

        public ValueTask DisposeAsync() => _stream.DisposeAsync();
    }

    private sealed class DisposedSocketConn : IRedisConnection
    {
        private readonly Socket _socket;
        private readonly Stream _stream;

        public DisposedSocketConn()
        {
            _socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            _socket.Dispose();
            _stream = new MemoryStream();
        }

        public Socket Socket => _socket;
        public Stream Stream => _stream;

        public ValueTask<Result<Unit>> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
            => ValueTask.FromResult<Result<Unit>>(Prelude.unit);

        public ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
            => ValueTask.FromResult<Result<int>>(0);

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NeverRespondingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.CompletedTask;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.CompletedTask;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var ex = new IOException("Simulated socket failure.", new SocketException((int)SocketError.ConnectionReset));
            return ValueTask.FromException<int>(ex);
        }
    }
}
