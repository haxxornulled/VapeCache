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

public sealed class RedisMultiplexedConnectionGenerationTests
{
    [Fact]
    public async Task ExecuteAsync_StaleLoopAfterReset_DoesNotCompleteWithStaleResponse()
    {
        var factory = new StaleLoopFactory();
        await using var mux = new RedisMultiplexedConnection(
            factory,
            maxInFlight: 1,
            coalesceWrites: false,
            responseTimeout: TimeSpan.FromSeconds(2));

        var pendingTask = mux.ExecuteAsync(RedisRespProtocol.PingCommand, CancellationToken.None).AsTask();

        await factory.Primary.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));
        await mux.ResetTransportAsync(new IOException("forced reset"));
        factory.Primary.ReleaseResponse();

        var completed = await Task.WhenAny(pendingTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(pendingTask, completed);

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await pendingTask);
        Assert.Contains("generation", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(factory.Primary.ResponseServed);
    }

    private sealed class StaleLoopFactory : IRedisConnectionFactory
    {
        private int _creates;
        public ControlledStaleResponseStream Primary { get; } = new();

        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
        {
            var index = Interlocked.Increment(ref _creates);
            IRedisConnection conn = index == 1
                ? new FakeConnection(Primary)
                : new FakeConnection(new NeverRespondingStream());

            return ValueTask.FromResult(new Result<IRedisConnection>(conn));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeConnection : IRedisConnection
    {
        private readonly Stream _stream;

        public FakeConnection(Stream stream)
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

    private sealed class ControlledStaleResponseStream : Stream
    {
        private static readonly byte[] Response = "+PONG\r\n"u8.ToArray();
        private readonly TaskCompletionSource<bool> _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _offset;
        private int _responseServed;

        public Task ReadStarted => _readStarted.Task;
        public bool ResponseServed => Volatile.Read(ref _responseServed) == 1;

        public void ReleaseResponse() => _release.TrySetResult(true);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.CompletedTask;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            if (_offset >= Response.Length)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return 0;
            }

            var take = Math.Min(buffer.Length, Response.Length - _offset);
            Response.AsSpan(_offset, take).CopyTo(buffer.Span);
            _offset += take;
            if (_offset >= Response.Length)
                Volatile.Write(ref _responseServed, 1);
            return take;
        }

        protected override void Dispose(bool disposing)
        {
            // Intentionally no-op: we want to simulate a stale in-flight read that can still surface bytes.
            base.Dispose(disposing);
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
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.CompletedTask;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }
}
