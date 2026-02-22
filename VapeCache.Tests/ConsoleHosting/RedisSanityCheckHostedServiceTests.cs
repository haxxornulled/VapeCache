using System.Net.Sockets;
using LanguageExt;
using LanguageExt.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Console.Hosting;

namespace VapeCache.Tests.ConsoleHosting;

public sealed class RedisSanityCheckHostedServiceTests
{
    [Fact]
    public async Task ExecuteAsync_clears_forced_open_when_ping_succeeds()
    {
        var options = Options.Create(new StartupPreflightOptions
        {
            Enabled = true,
            SanityCheckEnabled = true,
            SanityCheckInterval = TimeSpan.FromMilliseconds(10),
            SanityCheckTimeout = TimeSpan.FromMilliseconds(200),
            SanityCheckRetries = 0
        });
        var factory = new StubFactory(static _ =>
            ValueTask.FromResult<Result<IRedisConnection>>(new StubConnection(new PingPongStream())));
        var failover = new FakeFailoverController();
        failover.ForceOpen("startup-failed");

        var sut = new RedisSanityCheckHostedService(
            options,
            factory,
            failover,
            NullLogger<RedisSanityCheckHostedService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await sut.StartAsync(cts.Token);

        var cleared = await WaitUntilAsync(() => !failover.IsForcedOpen, TimeSpan.FromSeconds(1), cts.Token);

        await sut.StopAsync(CancellationToken.None);

        Assert.True(cleared);
        Assert.False(failover.IsForcedOpen);
        Assert.True(factory.Calls >= 1);
    }

    [Fact]
    public async Task ExecuteAsync_returns_without_checks_when_disabled()
    {
        var options = Options.Create(new StartupPreflightOptions
        {
            Enabled = false,
            SanityCheckEnabled = true,
            SanityCheckInterval = TimeSpan.FromMilliseconds(10)
        });
        var factory = new StubFactory(static _ =>
            ValueTask.FromResult<Result<IRedisConnection>>(new Result<IRedisConnection>(new InvalidOperationException("should not be called"))));
        var failover = new FakeFailoverController();
        failover.ForceOpen("forced");

        var sut = new RedisSanityCheckHostedService(
            options,
            factory,
            failover,
            NullLogger<RedisSanityCheckHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(30);
        await sut.StopAsync(CancellationToken.None);

        Assert.Equal(0, factory.Calls);
        Assert.True(failover.IsForcedOpen);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var stopAt = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < stopAt && !ct.IsCancellationRequested)
        {
            if (condition())
                return true;

            await Task.Delay(10, ct);
        }

        return condition();
    }

    private sealed class StubFactory(Func<CancellationToken, ValueTask<Result<IRedisConnection>>> create)
        : IRedisConnectionFactory
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public async ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return await create(ct);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubConnection(Stream stream) : IRedisConnection
    {
        public Socket Socket { get; } = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        public Stream Stream { get; } = stream;

        public ValueTask<Result<Unit>> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
            => ValueTask.FromResult<Result<Unit>>(Prelude.unit);

        public ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
            => ValueTask.FromResult<Result<int>>(0);

        public ValueTask DisposeAsync()
        {
            try { Stream.Dispose(); } catch { }
            try { Socket.Dispose(); } catch { }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PingPongStream : Stream
    {
        private readonly byte[] _response = "+PONG\r\n"u8.ToArray();
        private int _offset = int.MaxValue;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _response.Length;
        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= _response.Length)
                return ValueTask.FromResult(0);

            var take = Math.Min(buffer.Length, _response.Length - _offset);
            _response.AsSpan(_offset, take).CopyTo(buffer.Span);
            _offset += take;
            return ValueTask.FromResult(take);
        }

        public override void Write(byte[] buffer, int offset, int count)
            => _offset = 0;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _offset = 0;
            return ValueTask.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class FakeFailoverController : IRedisFailoverController
    {
        private string? _reason;

        public bool IsForcedOpen => !string.IsNullOrWhiteSpace(_reason);
        public string? Reason => _reason;

        public void ForceOpen(string reason) => _reason = reason;
        public void ClearForcedOpen() => _reason = null;
        public void MarkRedisSuccess() { }
        public void MarkRedisFailure() { }
    }
}
