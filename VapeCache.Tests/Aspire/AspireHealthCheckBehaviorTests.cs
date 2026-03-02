using System.Buffers;
using System.Net.Sockets;
using LanguageExt;
using LanguageExt.Common;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Extensions.Aspire.HealthChecks;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Tests.Aspire;

public sealed class AspireHealthCheckBehaviorTests
{
    [Fact]
    public async Task RedisHealthCheck_ReturnsHealthy_WhenPingSucceeds()
    {
        await using var pool = new FakePool(new Result<IRedisConnectionLease>(new FakeLease()));
        var healthCheck = new RedisHealthCheck(pool);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task RedisHealthCheck_ReturnsDegraded_WhenPoolTimesOut()
    {
        await using var pool = new FakePool(new Result<IRedisConnectionLease>(new TimeoutException("pool timed out")));
        var healthCheck = new RedisHealthCheck(pool);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("pool_timeout", result.Data["reason"]);
    }

    [Fact]
    public async Task VapeCacheHealthCheck_ReturnsDegraded_WhenServingFallback()
    {
        var current = new CurrentCacheService();
        var cache = new FakeCacheService(current, backendName: "memory");
        var stats = new FixedCacheStats(new CacheStatsSnapshot(10, 7, 3, 4, 1, 2, 1, 0, 0, 0));
        var breaker = new FakeBreakerState
        {
            Enabled = true,
            IsOpen = true,
            ConsecutiveFailures = 3,
            OpenRemaining = TimeSpan.FromSeconds(30)
        };
        var failover = new FakeFailoverController();
        var healthCheck = new VapeCacheHealthCheck(cache, current, stats, breaker, failover);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("memory", result.Data["current_backend"]);
        Assert.Equal(true, result.Data["breaker_open"]);
    }

    [Fact]
    public async Task VapeCacheHealthCheck_ReturnsHealthy_WhenRedisIsServing()
    {
        var current = new CurrentCacheService();
        var cache = new FakeCacheService(current, backendName: "redis");
        var stats = new FixedCacheStats(new CacheStatsSnapshot(4, 3, 1, 2, 0, 0, 0, 0, 0, 0));
        var breaker = new FakeBreakerState();
        var failover = new FakeFailoverController();
        var healthCheck = new VapeCacheHealthCheck(cache, current, stats, breaker, failover);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("redis", result.Data["current_backend"]);
        Assert.Equal(false, result.Data["breaker_open"]);
    }

    private sealed class FakePool(Result<IRedisConnectionLease> result) : IRedisConnectionPool
    {
        public ValueTask<Result<IRedisConnectionLease>> RentAsync(CancellationToken ct)
            => ValueTask.FromResult(result);

        public ValueTask DisposeAsync()
        {
            if (result.IsSuccess)
            {
                return result.Match(
                    static lease => lease.DisposeAsync(),
                    static _ => ValueTask.CompletedTask);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeLease : IRedisConnectionLease
    {
        private readonly FakeConnection _connection = new();

        public IRedisConnection Connection => _connection;

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    private sealed class FakeConnection : IRedisConnection
    {
        public Socket Socket { get; } = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        public Stream Stream { get; } = new PongStream();

        public ValueTask<Result<Unit>> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
            => ValueTask.FromResult<Result<Unit>>(Prelude.unit);

        public ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
            => ValueTask.FromResult<Result<int>>(0);

        public ValueTask DisposeAsync()
        {
            Stream.Dispose();
            Socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PongStream : Stream
    {
        private static readonly byte[] PongBytes = "+PONG\r\n"u8.ToArray();
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => PongBytes.Length;
        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_offset >= PongBytes.Length)
                return ValueTask.FromResult(0);

            var take = Math.Min(buffer.Length, PongBytes.Length - _offset);
            PongBytes.AsSpan(_offset, take).CopyTo(buffer.Span);
            _offset += take;
            return ValueTask.FromResult(take);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class FakeCacheService(ICurrentCacheService current, string backendName, Exception? failure = null) : ICacheService
    {
        public string Name => backendName;

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
        {
            current.SetCurrent(backendName);
            if (failure is not null)
                throw failure;
            return ValueTask.FromResult<byte[]?>(null);
        }

        public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask<bool> RemoveAsync(string key, CancellationToken ct)
            => ValueTask.FromResult(false);

        public ValueTask<T?> GetAsync<T>(string key, SpanDeserializer<T> deserialize, CancellationToken ct)
            => ValueTask.FromResult<T?>(default);

        public ValueTask SetAsync<T>(string key, T value, Action<IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct)
            => ValueTask.CompletedTask;

        public async ValueTask<T> GetOrSetAsync<T>(
            string key,
            Func<CancellationToken, ValueTask<T>> factory,
            Action<IBufferWriter<byte>, T> serialize,
            SpanDeserializer<T> deserialize,
            CacheEntryOptions options,
            CancellationToken ct)
            => await factory(ct).ConfigureAwait(false);
    }

    private sealed class FixedCacheStats(CacheStatsSnapshot snapshot) : ICacheStats
    {
        public CacheStatsSnapshot Snapshot { get; } = snapshot;
    }

    private sealed class FakeBreakerState : IRedisCircuitBreakerState
    {
        public bool Enabled { get; init; }
        public bool IsOpen { get; init; }
        public int ConsecutiveFailures { get; init; }
        public TimeSpan? OpenRemaining { get; init; }
        public bool HalfOpenProbeInFlight { get; init; }
    }

    private sealed class FakeFailoverController : IRedisFailoverController
    {
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

        public void MarkRedisSuccess()
        {
        }

        public void MarkRedisFailure()
        {
        }
    }
}
