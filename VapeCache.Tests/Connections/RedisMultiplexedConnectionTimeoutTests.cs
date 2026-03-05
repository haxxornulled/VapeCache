using System;
using System.IO;
using System.Net;
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
    public async Task ExecuteAsync_BulkTimeout_DoesNotResetTransport()
    {
        var factory = new NeverRespondingFactory();
        var mux = new RedisMultiplexedConnection(
            factory,
            maxInFlight: 1,
            coalesceWrites: false,
            responseTimeout: TimeSpan.FromMilliseconds(50),
            bulkPayloadBytesThreshold: 4096,
            fastTimeoutResetThreshold: 1,
            fastTimeoutResetWindow: TimeSpan.FromSeconds(5));

        try
        {
            var command = CreateLargeSetCommand(16 * 1024);
            var task = mux.ExecuteAsync(command, CancellationToken.None).AsTask();
            var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(task, completed);
            await Assert.ThrowsAsync<TimeoutException>(() => task);

            Assert.Equal(1, mux.ResponseTimeoutCount);
            Assert.Equal(0, mux.FailureCount);
            Assert.Equal(1, factory.CreateCount);
        }
        finally
        {
            await mux.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecuteAsync_BulkTimeout_WithSocketReader_ResetsTransport()
    {
        await using var factory = await NeverRespondingSocketFactory.StartAsync();
        var mux = new RedisMultiplexedConnection(
            factory,
            maxInFlight: 1,
            coalesceWrites: false,
            enableSocketRespReader: true,
            responseTimeout: TimeSpan.FromMilliseconds(50),
            bulkPayloadBytesThreshold: 4096,
            fastTimeoutResetThreshold: 99,
            fastTimeoutResetWindow: TimeSpan.FromSeconds(5));

        try
        {
            var command = CreateLargeSetCommand(16 * 1024);
            var task = mux.ExecuteAsync(command, CancellationToken.None).AsTask();
            var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(task, completed);
            await Assert.ThrowsAsync<TimeoutException>(() => task);

            Assert.Equal(1, mux.ResponseTimeoutCount);
            Assert.True(mux.FailureCount >= 1);
            Assert.True(factory.CreateCount >= 1);
        }
        finally
        {
            await mux.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecuteAsync_FastTimeoutBurst_ResetsTransportAfterThreshold()
    {
        var factory = new NeverRespondingFactory();
        var mux = new RedisMultiplexedConnection(
            factory,
            maxInFlight: 1,
            coalesceWrites: false,
            responseTimeout: TimeSpan.FromMilliseconds(40),
            fastTimeoutResetThreshold: 2,
            fastTimeoutResetWindow: TimeSpan.FromSeconds(2));

        try
        {
            var first = mux.ExecuteAsync(RedisRespProtocol.PingCommand, CancellationToken.None).AsTask();
            var firstDone = await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(first, firstDone);
            await Assert.ThrowsAsync<TimeoutException>(() => first);
            Assert.Equal(0, mux.FailureCount);

            var second = mux.ExecuteAsync(RedisRespProtocol.PingCommand, CancellationToken.None).AsTask();
            var secondDone = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(second, secondDone);
            await Assert.ThrowsAsync<TimeoutException>(() => second);

            Assert.True(mux.FailureCount >= 1);
            Assert.True(mux.ResponseTimeoutCount >= 2);

            var third = mux.ExecuteAsync(RedisRespProtocol.PingCommand, CancellationToken.None).AsTask();
            var thirdDone = await Task.WhenAny(third, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(third, thirdDone);
            await Assert.ThrowsAsync<TimeoutException>(() => third);

            Assert.True(factory.CreateCount >= 2);
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
        private int _creates;

        public int CreateCount => Volatile.Read(ref _creates);

        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _creates);
            return ValueTask.FromResult(new Result<IRedisConnection>(new FakeConn(new NeverRespondingStream())));
        }

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

    private sealed class NeverRespondingSocketFactory : IRedisConnectionFactory
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _acceptCts = new();
        private readonly object _gate = new();
        private readonly List<Socket> _acceptedSockets = new();
        private readonly Task _acceptLoopTask;
        private int _creates;

        private NeverRespondingSocketFactory(TcpListener listener)
        {
            _listener = listener;
            _acceptLoopTask = AcceptLoopAsync();
        }

        public int CreateCount => Volatile.Read(ref _creates);

        public static Task<NeverRespondingSocketFactory> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new NeverRespondingSocketFactory(listener));
        }

        public async ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _creates);
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
            return new Result<IRedisConnection>(new LoopbackSocketConnection(socket));
        }

        public async ValueTask DisposeAsync()
        {
            _acceptCts.Cancel();
            try { _listener.Stop(); } catch { }
            try { await _acceptLoopTask.ConfigureAwait(false); } catch { }

            Socket[] sockets;
            lock (_gate)
            {
                sockets = _acceptedSockets.ToArray();
                _acceptedSockets.Clear();
            }

            for (var i = 0; i < sockets.Length; i++)
            {
                try { sockets[i].Dispose(); } catch { }
            }

            _acceptCts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_acceptCts.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await _listener.AcceptSocketAsync(_acceptCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_acceptCts.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (_acceptCts.IsCancellationRequested)
                        break;
                    continue;
                }

                lock (_gate)
                {
                    _acceptedSockets.Add(socket);
                }
            }
        }
    }

    private sealed class LoopbackSocketConnection : IRedisConnection
    {
        private readonly Socket _socket;
        private readonly NetworkStream _stream;
        private int _disposed;

        public LoopbackSocketConnection(Socket socket)
        {
            _socket = socket;
            _stream = new NetworkStream(socket, ownsSocket: false);
        }

        public Socket Socket => _socket;
        public Stream Stream => _stream;

        public async ValueTask<Result<Unit>> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) == 1)
                return new Result<Unit>(new ObjectDisposedException(nameof(LoopbackSocketConnection)));

            try
            {
                var total = 0;
                while (total < buffer.Length)
                {
                    var sent = await _socket.SendAsync(buffer.Slice(total), SocketFlags.None, ct).ConfigureAwait(false);
                    if (sent <= 0)
                        return new Result<Unit>(new IOException("Socket send returned 0."));
                    total += sent;
                }

                return Prelude.unit;
            }
            catch (Exception ex)
            {
                return new Result<Unit>(ex);
            }
        }

        public async ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) == 1)
                return new Result<int>(new ObjectDisposedException(nameof(LoopbackSocketConnection)));

            try
            {
                var received = await _socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
                return received;
            }
            catch (Exception ex)
            {
                return new Result<int>(ex);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return ValueTask.CompletedTask;

            try { _stream.Dispose(); } catch { }
            try { _socket.Dispose(); } catch { }
            return ValueTask.CompletedTask;
        }
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

    private static ReadOnlyMemory<byte> CreateLargeSetCommand(int valueBytes)
    {
        var value = GC.AllocateUninitializedArray<byte>(valueBytes);
        value.AsSpan().Fill((byte)'x');
        var len = RedisRespProtocol.GetSetCommandLength("timeout:bulk", valueBytes, ttlMs: null);
        var command = GC.AllocateUninitializedArray<byte>(len);
        var written = RedisRespProtocol.WriteSetCommand(command, "timeout:bulk", value, ttlMs: null);
        return command.AsMemory(0, written);
    }
}
