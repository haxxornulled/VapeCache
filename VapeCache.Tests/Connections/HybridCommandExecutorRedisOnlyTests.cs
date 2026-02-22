using System.IO;
using System.Net.Sockets;
using LanguageExt;
using LanguageExt.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;
using Xunit;

namespace VapeCache.Tests.Connections;

public sealed class HybridCommandExecutorRedisOnlyTests
{
    [Fact]
    public async Task ModuleList_UsesFallback_WhenBreakerOpen()
    {
        await using var redis = CreateRedisExecutor();
        await using var fallback = new InMemoryCommandExecutor();
        var breaker = new FakeBreaker
        {
            Enabled = true,
            IsOpen = true,
            OpenRemaining = TimeSpan.FromSeconds(30)
        };

        var hybrid = new HybridCommandExecutor(
            redis,
            fallback,
            breaker,
            breaker,
            new CacheStatsRegistry(),
            new CurrentCacheService(),
            Options.Create(new RedisCircuitBreakerOptions { Enabled = true }),
            NullLogger<HybridCommandExecutor>.Instance);

        var modules = await hybrid.ModuleListAsync(CancellationToken.None);
        Assert.Empty(modules);
    }

    [Fact]
    public async Task Ping_UsesFallback_WhenBreakerOpen()
    {
        await using var redis = CreateRedisExecutor();
        await using var fallback = new InMemoryCommandExecutor();
        var breaker = new FakeBreaker
        {
            Enabled = true,
            IsOpen = true,
            OpenRemaining = TimeSpan.FromSeconds(5)
        };

        var hybrid = new HybridCommandExecutor(
            redis,
            fallback,
            breaker,
            breaker,
            new CacheStatsRegistry(),
            new CurrentCacheService(),
            Options.Create(new RedisCircuitBreakerOptions { Enabled = true }),
            NullLogger<HybridCommandExecutor>.Instance);

        var result = await hybrid.PingAsync(CancellationToken.None);
        Assert.Equal("PONG", result);
    }

    [Fact]
    public async Task Get_propagates_user_cancellation()
    {
        await using var redis = CreateRedisExecutor();
        await using var fallback = new InMemoryCommandExecutor();
        var breaker = new FakeBreaker { Enabled = true, IsOpen = false };

        var hybrid = new HybridCommandExecutor(
            redis,
            fallback,
            breaker,
            breaker,
            new CacheStatsRegistry(),
            new CurrentCacheService(),
            Options.Create(new RedisCircuitBreakerOptions { Enabled = true }),
            NullLogger<HybridCommandExecutor>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hybrid.GetAsync("cancel", cts.Token).AsTask());
    }

    private static RedisCommandExecutor CreateRedisExecutor()
    {
        var factory = new NoopConnectionFactory();
        return new RedisCommandExecutor(factory, Options.Create(new RedisMultiplexerOptions()));
    }

    private sealed class FakeBreaker : IRedisCircuitBreakerState, IRedisFailoverController
    {
        public bool Enabled { get; set; }
        public bool IsOpen { get; set; }
        public int ConsecutiveFailures { get; set; }
        public TimeSpan? OpenRemaining { get; set; }
        public bool HalfOpenProbeInFlight { get; set; }
        public bool IsForcedOpen { get; private set; }
        public string? Reason { get; private set; }

        public void ForceOpen(string reason)
        {
            IsForcedOpen = true;
            Reason = reason;
        }

        public void ClearForcedOpen()
        {
            IsForcedOpen = false;
            Reason = null;
        }

        public void MarkRedisSuccess() { }
        public void MarkRedisFailure() { }
    }

    private sealed class NoopConnectionFactory : IRedisConnectionFactory
    {
        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
            => ValueTask.FromResult(new Result<IRedisConnection>(new NoopConnection()));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopConnection : IRedisConnection
    {
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private readonly MemoryStream _stream = new();

        public Socket Socket => _socket;
        public Stream Stream => _stream;

        public ValueTask<Result<Unit>> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
            => ValueTask.FromResult(new Result<Unit>(Prelude.unit));

        public ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
            => ValueTask.FromResult(new Result<int>(0));

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            _socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
