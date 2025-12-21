using LanguageExt;
using LanguageExt.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net.Sockets;
using VapeCache.Application.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests;

public sealed class RedisConnectionPoolTests
{
    [Fact]
    public async Task Rent_returns_connection_and_reuses_on_return()
    {
        var factory = new FakeFactory();
        var options = Options.Create(new RedisConnectionOptions { Host = "h", MaxConnections = 2, MaxIdle = 2, AcquireTimeout = TimeSpan.FromSeconds(1) });

        await using var pool = new RedisConnectionPool(factory, new OptionsMonitorStub<RedisConnectionOptions>(options.Value), NullLogger<RedisConnectionPool>.Instance);

        var first = await pool.RentAsync(CancellationToken.None);
        Assert.True(first.IsSuccess);

        await first.Match(
            async lease => await lease.DisposeAsync(),
            ex => throw ex);

        var second = await pool.RentAsync(CancellationToken.None);
        Assert.True(second.IsSuccess);

        Assert.Equal(1, factory.CreatedCount);
    }

    [Fact]
    public async Task Rent_blocks_when_at_capacity_until_returned()
    {
        var factory = new FakeFactory();
        var options = new RedisConnectionOptions { Host = "h", MaxConnections = 2, MaxIdle = 2, AcquireTimeout = TimeSpan.FromSeconds(2) };

        await using var pool = new RedisConnectionPool(factory, new OptionsMonitorStub<RedisConnectionOptions>(options), NullLogger<RedisConnectionPool>.Instance);

        var r1 = await pool.RentAsync(CancellationToken.None);
        var r2 = await pool.RentAsync(CancellationToken.None);
        Assert.True(r1.IsSuccess);
        Assert.True(r2.IsSuccess);
        Assert.Equal(2, factory.CreatedCount);

        var rent3Task = pool.RentAsync(CancellationToken.None).AsTask();

        await Task.Delay(100);
        Assert.False(rent3Task.IsCompleted);

        await r1.Match(
            async lease => await lease.DisposeAsync(),
            ex => throw ex);

        var r3 = await rent3Task;
        Assert.True(r3.IsSuccess);
        Assert.Equal(2, factory.CreatedCount);
    }

    [Fact]
    public async Task Rent_times_out_when_at_capacity_and_none_returned()
    {
        var factory = new FakeFactory();
        var options = new RedisConnectionOptions { Host = "h", MaxConnections = 1, MaxIdle = 1, AcquireTimeout = TimeSpan.FromMilliseconds(150) };

        await using var pool = new RedisConnectionPool(factory, new OptionsMonitorStub<RedisConnectionOptions>(options), NullLogger<RedisConnectionPool>.Instance);

        var r1 = await pool.RentAsync(CancellationToken.None);
        Assert.True(r1.IsSuccess);
        Assert.Equal(1, factory.CreatedCount);

        var r2 = await pool.RentAsync(CancellationToken.None);
        Assert.False(r2.IsSuccess);

        var ex = r2.Match<Exception>(_ => new Exception("Expected RentAsync to fail."), e => e);
        Assert.IsType<TimeoutException>(ex);
    }

    private sealed class FakeFactory : IRedisConnectionFactory
    {
        public int CreatedCount { get; private set; }

        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
        {
            CreatedCount++;
            return ValueTask.FromResult(new Result<IRedisConnection>(new FakeConnection()));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeConnection : IRedisConnection
    {
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private readonly MemoryStream _stream = new();

        public Socket Socket => _socket;
        public Stream Stream => _stream;

        public ValueTask<Result<Unit>> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct) =>
            ValueTask.FromResult(new Result<Unit>(Prelude.unit));

        public ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct) =>
            ValueTask.FromResult(new Result<int>(0));

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            _socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OptionsMonitorStub<T>(T value) : IOptionsMonitor<T>
        where T : class
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
