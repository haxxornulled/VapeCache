using System.Net.Sockets;
using LanguageExt;
using LanguageExt.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Console.Hosting;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Tests.ConsoleHosting;

public sealed class StartupPreflightHostedServiceTests
{
    [Fact]
    public async Task StartAsync_returns_immediately_when_disabled()
    {
        var options = Options.Create(new StartupPreflightOptions { Enabled = false });
        var factory = new StubFactory(static _ =>
            ValueTask.FromResult<Result<IRedisConnection>>(new Result<IRedisConnection>(new InvalidOperationException("should not be called"))));
        var failover = new FakeFailoverController();
        var current = new CurrentCacheService();

        var sut = new StartupPreflightHostedService(
            options,
            factory,
            failover,
            current,
            NullLogger<StartupPreflightHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);

        Assert.Equal(0, factory.Calls);
        Assert.False(failover.IsForcedOpen);
    }

    [Fact]
    public async Task StartAsync_failfast_false_forces_memory_on_failures()
    {
        var options = Options.Create(new StartupPreflightOptions
        {
            Enabled = true,
            Connections = 2,
            ValidatePing = false,
            FailFast = false,
            FailoverToMemoryOnFailure = true,
            Timeout = TimeSpan.FromSeconds(1)
        });
        var factory = new StubFactory(static _ =>
            ValueTask.FromResult<Result<IRedisConnection>>(new Result<IRedisConnection>(new InvalidOperationException("redis down"))));
        var failover = new FakeFailoverController();
        var current = new CurrentCacheService();

        var sut = new StartupPreflightHostedService(
            options,
            factory,
            failover,
            current,
            NullLogger<StartupPreflightHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);

        Assert.True(failover.IsForcedOpen);
        Assert.Equal("memory", current.CurrentName);
        Assert.Equal("startup-preflight-failed", failover.Reason);
    }

    [Fact]
    public async Task StartAsync_failfast_true_throws_when_all_connections_fail()
    {
        var options = Options.Create(new StartupPreflightOptions
        {
            Enabled = true,
            Connections = 1,
            ValidatePing = false,
            FailFast = true,
            Timeout = TimeSpan.FromSeconds(1)
        });
        var factory = new StubFactory(static _ =>
            ValueTask.FromResult<Result<IRedisConnection>>(new Result<IRedisConnection>(new InvalidOperationException("redis down"))));
        var failover = new FakeFailoverController();
        var current = new CurrentCacheService();

        var sut = new StartupPreflightHostedService(
            options,
            factory,
            failover,
            current,
            NullLogger<StartupPreflightHostedService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.StartAsync(CancellationToken.None));
        Assert.False(failover.IsForcedOpen);
    }

    [Fact]
    public async Task StartAsync_with_validate_ping_succeeds_for_pong()
    {
        var options = Options.Create(new StartupPreflightOptions
        {
            Enabled = true,
            Connections = 1,
            ValidatePing = true,
            FailFast = true,
            Timeout = TimeSpan.FromSeconds(1)
        });
        var factory = new StubFactory(static _ =>
            ValueTask.FromResult<Result<IRedisConnection>>(new StubConnection(new PingPongStream(pong: true))));
        var failover = new FakeFailoverController();
        var current = new CurrentCacheService();

        var sut = new StartupPreflightHostedService(
            options,
            factory,
            failover,
            current,
            NullLogger<StartupPreflightHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);

        Assert.Equal(1, factory.Calls);
        Assert.False(failover.IsForcedOpen);
    }

    [Fact]
    public async Task StartAsync_timeout_with_failfast_false_forces_memory()
    {
        var options = Options.Create(new StartupPreflightOptions
        {
            Enabled = true,
            Connections = 1,
            ValidatePing = false,
            FailFast = false,
            FailoverToMemoryOnFailure = true,
            Timeout = TimeSpan.FromMilliseconds(30)
        });
        var factory = new StubFactory(static async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new Result<IRedisConnection>(new InvalidOperationException("unreachable"));
        });
        var failover = new FakeFailoverController();
        var current = new CurrentCacheService();

        var sut = new StartupPreflightHostedService(
            options,
            factory,
            failover,
            current,
            NullLogger<StartupPreflightHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);

        Assert.True(failover.IsForcedOpen);
        Assert.Equal("startup-preflight-timeout", failover.Reason);
        Assert.Equal("memory", current.CurrentName);
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

    private sealed class PingPongStream(bool pong) : Stream
    {
        private readonly byte[] _response = pong ? "+PONG\r\n"u8.ToArray() : "-ERR nope\r\n"u8.ToArray();
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
