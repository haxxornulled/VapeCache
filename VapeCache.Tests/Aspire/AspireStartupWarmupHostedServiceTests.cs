using System.Net.Sockets;
using LanguageExt;
using LanguageExt.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VapeCache.Abstractions.Connections;
using VapeCache.Extensions.Aspire;

namespace VapeCache.Tests.Aspire;

public sealed class AspireStartupWarmupHostedServiceTests
{
    [Fact]
    public async Task StartupWarmup_MarksReady_WhenPoolWarmupSucceeds()
    {
        var hostBuilder = new HostApplicationBuilder();
        hostBuilder.Services.AddSingleton<IRedisConnectionPool, SuccessfulPool>();
        hostBuilder.AddVapeCacheClientBuilder(registerCoreServices: false)
            .WithStartupWarmup(options =>
            {
                options.ConnectionsToWarm = 4;
                options.RequiredSuccessfulConnections = 2;
                options.ValidatePing = true;
                options.Timeout = TimeSpan.FromSeconds(5);
                options.FailFastOnWarmupFailure = false;
            });

        using var host = hostBuilder.Build();
        await host.StartAsync();

        var readiness = host.Services.GetRequiredService<IVapeCacheStartupReadiness>();
        Assert.True(readiness.IsReady);
        Assert.False(readiness.IsRunning);
        Assert.Equal(4, readiness.TargetConnections);
        Assert.Equal(4, readiness.SuccessfulConnections);
        Assert.Equal(0, readiness.FailedConnections);
        Assert.NotNull(readiness.CompletedAtUtc);

        await host.StopAsync();
    }

    [Fact]
    public async Task StartupWarmup_DoesNotFailStartup_WhenFailFastDisabled()
    {
        var hostBuilder = new HostApplicationBuilder();
        hostBuilder.Services.AddSingleton<IRedisConnectionPool>(_ => new FailingPool(new TimeoutException("pool-timeout")));
        hostBuilder.AddVapeCacheClientBuilder(registerCoreServices: false)
            .WithStartupWarmup(options =>
            {
                options.ConnectionsToWarm = 3;
                options.RequiredSuccessfulConnections = 2;
                options.FailFastOnWarmupFailure = false;
                options.Timeout = TimeSpan.FromSeconds(2);
            });

        using var host = hostBuilder.Build();
        await host.StartAsync();

        var readiness = host.Services.GetRequiredService<IVapeCacheStartupReadiness>();
        Assert.False(readiness.IsReady);
        Assert.False(readiness.IsRunning);
        Assert.Equal(0, readiness.SuccessfulConnections);
        Assert.Equal(3, readiness.FailedConnections);
        Assert.NotNull(readiness.LastError);

        await host.StopAsync();
    }

    [Fact]
    public async Task StartupWarmup_FailFast_Throws_WhenReadinessIsNotReached()
    {
        var hostBuilder = new HostApplicationBuilder();
        hostBuilder.Services.AddSingleton<IRedisConnectionPool>(_ => new FailingPool(new InvalidOperationException("no-redis")));
        hostBuilder.AddVapeCacheClientBuilder(registerCoreServices: false)
            .WithStartupWarmup(options =>
            {
                options.ConnectionsToWarm = 2;
                options.RequiredSuccessfulConnections = 2;
                options.FailFastOnWarmupFailure = true;
                options.Timeout = TimeSpan.FromSeconds(2);
            });

        using var host = hostBuilder.Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
    }

    private sealed class SuccessfulPool : IRedisConnectionPool
    {
        public ValueTask<Result<IRedisConnectionLease>> RentAsync(CancellationToken ct)
            => ValueTask.FromResult<Result<IRedisConnectionLease>>(new SuccessfulLease());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingPool(Exception exception) : IRedisConnectionPool
    {
        public ValueTask<Result<IRedisConnectionLease>> RentAsync(CancellationToken ct)
            => ValueTask.FromResult(new Result<IRedisConnectionLease>(exception));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SuccessfulLease : IRedisConnectionLease
    {
        private readonly SuccessfulConnection _connection = new();

        public IRedisConnection Connection => _connection;

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    private sealed class SuccessfulConnection : IRedisConnection
    {
        private static readonly byte[] Pong = "+PONG\r\n"u8.ToArray();
        private int _readOffset;

        public Socket Socket { get; } = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        public Stream Stream => Stream.Null;

        public ValueTask<Result<Unit>> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
            => ValueTask.FromResult<Result<Unit>>(Prelude.unit);

        public ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (_readOffset >= Pong.Length)
                return ValueTask.FromResult<Result<int>>(0);

            var count = Math.Min(buffer.Length, Pong.Length - _readOffset);
            Pong.AsMemory(_readOffset, count).CopyTo(buffer);
            _readOffset += count;
            return ValueTask.FromResult<Result<int>>(count);
        }

        public ValueTask DisposeAsync()
        {
            Socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
