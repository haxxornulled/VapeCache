using System.Net.Sockets;
using LanguageExt;
using LanguageExt.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Console.Stress;
using VapeCache.Infrastructure.Connections;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.ConsoleStress;

public sealed class RedisStressHostedServiceTests
{
    [Fact]
    public async Task StartAsync_noop_when_disabled()
    {
        using var services = new ServiceCollection()
            .AddSingleton<IRedisConnectionFactory>(new DummyFactory())
            .AddSingleton<IRedisCommandExecutor>(new InMemoryCommandExecutor())
            .BuildServiceProvider();

        var stress = Options.Create(new RedisStressOptions { Enabled = false });
        var redisOptions = new TestOptionsMonitor<RedisConnectionOptions>(new RedisConnectionOptions { Host = "localhost" });
        var lifetime = new TestLifetime();

        var sut = new RedisStressHostedService(
            stress,
            redisOptions,
            services,
            lifetime,
            NullLogger<RedisStressHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(30);
        await sut.StopAsync(CancellationToken.None);

        Assert.Equal(0, lifetime.StopCalls);
    }

    [Fact]
    public async Task RunAsync_mux_mode_completes_and_requests_host_stop()
    {
        using var services = new ServiceCollection()
            .AddSingleton<IRedisConnectionFactory>(new DummyFactory())
            .AddSingleton<IRedisCommandExecutor>(new InMemoryCommandExecutor())
            .BuildServiceProvider();

        var stress = Options.Create(new RedisStressOptions
        {
            Enabled = true,
            Mode = "mux",
            Workload = "payload",
            Workers = 1,
            Duration = TimeSpan.FromMilliseconds(120),
            VirtualUsers = 16,
            PayloadBytes = 16,
            KeyPrefix = "stress:test:",
            KeySpace = 32,
            SetPercent = 50,
            PreloadKeys = true,
            LogEvery = TimeSpan.FromMilliseconds(20),
            OperationTimeout = TimeSpan.FromMilliseconds(100),
            BurstRequests = 8
        });

        var redisOptions = new TestOptionsMonitor<RedisConnectionOptions>(new RedisConnectionOptions { Host = "localhost" });
        var lifetime = new TestLifetime();

        var sut = new RedisStressHostedService(
            stress,
            redisOptions,
            services,
            lifetime,
            NullLogger<RedisStressHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);

        var stopped = await WaitUntilAsync(() => lifetime.StopCalls > 0, TimeSpan.FromSeconds(3));

        await sut.StopAsync(CancellationToken.None);

        Assert.True(stopped);
        Assert.True(lifetime.StopCalls >= 1);
    }

    [Fact]
    public async Task RunAsync_pool_mode_rents_connections_and_stops()
    {
        var pool = new PingPool();
        var factory = new PingFactory();
        using var services = new ServiceCollection()
            .AddSingleton<IRedisConnectionFactory>(factory)
            .AddSingleton<IRedisConnectionPool>(pool)
            .BuildServiceProvider();

        var stress = Options.Create(new RedisStressOptions
        {
            Enabled = true,
            Mode = "pool",
            Workers = 1,
            Duration = TimeSpan.FromMilliseconds(120),
            OperationsPerLease = 1,
            LogEvery = TimeSpan.FromMilliseconds(20),
            OperationTimeout = TimeSpan.FromMilliseconds(200)
        });
        var redisOptions = new TestOptionsMonitor<RedisConnectionOptions>(new RedisConnectionOptions { Host = "localhost" });
        var lifetime = new TestLifetime();

        var sut = new RedisStressHostedService(
            stress,
            redisOptions,
            services,
            lifetime,
            NullLogger<RedisStressHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        var stopped = await WaitUntilAsync(() => lifetime.StopCalls > 0, TimeSpan.FromSeconds(3));
        await sut.StopAsync(CancellationToken.None);

        Assert.True(stopped);
        Assert.True(pool.RentCalls > 0);
        Assert.Equal(0, factory.CreateCalls); // pool mode should not create via factory
    }

    [Fact]
    public async Task RunAsync_burn_mode_honors_burn_target()
    {
        var factory = new PingFactory();
        using var services = new ServiceCollection()
            .AddSingleton<IRedisConnectionFactory>(factory)
            .BuildServiceProvider();

        var stress = Options.Create(new RedisStressOptions
        {
            Enabled = true,
            Mode = "burn",
            Workers = 1,
            BurnConnectionsTarget = 3,
            BurnLogEvery = 1,
            LogEvery = TimeSpan.FromMilliseconds(20),
            OperationTimeout = TimeSpan.FromMilliseconds(200)
        });
        var redisOptions = new TestOptionsMonitor<RedisConnectionOptions>(new RedisConnectionOptions { Host = "localhost" });
        var lifetime = new TestLifetime();

        var sut = new RedisStressHostedService(
            stress,
            redisOptions,
            services,
            lifetime,
            NullLogger<RedisStressHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        var stopped = await WaitUntilAsync(() => lifetime.StopCalls > 0, TimeSpan.FromSeconds(3));
        await sut.StopAsync(CancellationToken.None);

        Assert.True(stopped);
        Assert.True(factory.CreateCalls >= 3);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stopAt = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < stopAt)
        {
            if (predicate())
                return true;

            await Task.Delay(15);
        }

        return predicate();
    }

    private sealed class DummyFactory : IRedisConnectionFactory
    {
        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
            => ValueTask.FromResult(new Result<IRedisConnection>(new InvalidOperationException("not used in mux mode")));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PingFactory : IRedisConnectionFactory
    {
        private int _createCalls;
        public int CreateCalls => Volatile.Read(ref _createCalls);

        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _createCalls);
            return ValueTask.FromResult<Result<IRedisConnection>>(new Result<IRedisConnection>(new PingConnection()));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PingPool : IRedisConnectionPool
    {
        private int _rentCalls;
        public int RentCalls => Volatile.Read(ref _rentCalls);

        public ValueTask<Result<IRedisConnectionLease>> RentAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _rentCalls);
            return ValueTask.FromResult<Result<IRedisConnectionLease>>(new Result<IRedisConnectionLease>(new Lease(new PingConnection())));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Lease(IRedisConnection connection) : IRedisConnectionLease
    {
        public IRedisConnection Connection { get; } = connection;

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    private sealed class PingConnection : IRedisConnection
    {
        private readonly byte[] _pong = "+PONG\r\n"u8.ToArray();
        private int _offset = int.MaxValue;

        public Socket Socket { get; } = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        public Stream Stream { get; } = Stream.Null;

        public ValueTask<Result<Unit>> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
        {
            _offset = 0;
            return ValueTask.FromResult<Result<Unit>>(Prelude.unit);
        }

        public ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (_offset >= _pong.Length)
                return ValueTask.FromResult<Result<int>>(0);

            buffer.Span[0] = _pong[_offset++];
            return ValueTask.FromResult<Result<int>>(1);
        }

        public ValueTask DisposeAsync()
        {
            try { Socket.Dispose(); } catch { }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();
        private int _stopCalls;

        public int StopCalls => Volatile.Read(ref _stopCalls);

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            Interlocked.Increment(ref _stopCalls);
            try { _stopping.Cancel(); } catch { }
            try { _stopped.Cancel(); } catch { }
        }
    }
}
