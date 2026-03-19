using System.IO;
using System.Net.Sockets;
using System.Collections.Concurrent;
using LanguageExt;
using LanguageExt.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;
using VapeCache.Tests.Infrastructure;
using Xunit;

namespace VapeCache.Tests.Connections;

public sealed class HybridCommandExecutorRedisOnlyTests
{
    [Fact]
    public async Task ModuleList_BypassesFallback_WhenBreakerOpen()
    {
        await using var redis = CreateRedisExecutor(new StaticResponseConnectionFactory("*1\r\n*2\r\n:0\r\n$6\r\nsearch\r\n"));
        await using var fallback = new InMemoryCommandExecutor();
        var stats = new CacheStatsRegistry();
        var current = new CurrentCacheService();
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
            stats,
            current,
            new TestOptionsMonitor<RedisCircuitBreakerOptions>(new RedisCircuitBreakerOptions { Enabled = true }),
            NullLogger<HybridCommandExecutor>.Instance);

        var modules = await hybrid.ModuleListAsync(CancellationToken.None);
        var module = Assert.Single(modules);
        Assert.Equal("search", module);
        Assert.Equal(0, stats.GetOrCreate(CacheStatsNames.Hybrid).Snapshot.FallbackToMemory);
        Assert.Equal("redis", current.CurrentName);
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
            new TestOptionsMonitor<RedisCircuitBreakerOptions>(new RedisCircuitBreakerOptions { Enabled = true }),
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
            new TestOptionsMonitor<RedisCircuitBreakerOptions>(new RedisCircuitBreakerOptions { Enabled = true }),
            NullLogger<HybridCommandExecutor>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hybrid.GetAsync("cancel", cts.Token).AsTask());
    }

    [Fact]
    public async Task Scan_does_not_replay_fallback_after_partial_primary_enumeration()
    {
        var stream = new ScriptedResponseStream();
        stream.Enqueue("*2\r\n$1\r\n1\r\n*1\r\n$2\r\nk1\r\n");
        stream.Enqueue("-ERR scan failed\r\n");

        await using var redis = CreateRedisExecutor(new SingleConnectionFactory(new ScriptedConnection(stream)));
        await using var fallback = new InMemoryCommandExecutor();
        await fallback.SetAsync("k1", "fallback-1"u8.ToArray(), null, CancellationToken.None);
        await fallback.SetAsync("k2", "fallback-2"u8.ToArray(), null, CancellationToken.None);

        var breaker = new FakeBreaker { Enabled = true, IsOpen = false };
        var hybrid = new HybridCommandExecutor(
            redis,
            fallback,
            breaker,
            breaker,
            new CacheStatsRegistry(),
            new CurrentCacheService(),
            new TestOptionsMonitor<RedisCircuitBreakerOptions>(new RedisCircuitBreakerOptions { Enabled = true }),
            NullLogger<HybridCommandExecutor>.Instance);

        var items = new List<string>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var key in hybrid.ScanAsync("k*", 10, CancellationToken.None))
                items.Add(key);
        });

        Assert.Equal(["k1"], items);
        Assert.Contains("scan failed", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, breaker.MarkRedisFailureCalls);
    }

    private static RedisCommandExecutor CreateRedisExecutor(IRedisConnectionFactory? factory = null)
    {
        factory ??= new NoopConnectionFactory();
        return new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions
            {
                Connections = 1,
                MaxInFlightPerConnection = 8,
                EnableCoalescedSocketWrites = false,
                EnableCommandInstrumentation = false,
                ResponseTimeout = TimeSpan.FromSeconds(2)
            }));
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
        public int MarkRedisFailureCalls { get; private set; }
        public void MarkRedisFailure() => MarkRedisFailureCalls++;
    }

    private sealed class NoopConnectionFactory : IRedisConnectionFactory
    {
        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
            => ValueTask.FromResult(new Result<IRedisConnection>(new NoopConnection()));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StaticResponseConnectionFactory(string response) : IRedisConnectionFactory
    {
        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
            => ValueTask.FromResult(new Result<IRedisConnection>(new StaticResponseConnection(response)));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SingleConnectionFactory(IRedisConnection connection) : IRedisConnectionFactory
    {
        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
            => ValueTask.FromResult(new Result<IRedisConnection>(connection));

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

    private sealed class StaticResponseConnection : IRedisConnection
    {
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private readonly MemoryStream _stream;

        public StaticResponseConnection(string response)
        {
            _stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(response));
        }

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

    private sealed class ScriptedConnection(ScriptedResponseStream stream) : IRedisConnection
    {
        public Socket Socket { get; } = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        public Stream Stream { get; } = stream;

        public ValueTask<Result<Unit>> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
            => ValueTask.FromResult<Result<Unit>>(Prelude.unit);

        public ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
            => ValueTask.FromResult<Result<int>>(0);

        public ValueTask DisposeAsync()
        {
            Socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedResponseStream : Stream
    {
        private readonly ConcurrentQueue<byte[]> _frames = new();
        private byte[]? _current;
        private int _offset;

        public void Enqueue(string frame)
            => Enqueue(System.Text.Encoding.UTF8.GetBytes(frame));

        public void Enqueue(byte[] frame)
            => _frames.Enqueue(frame);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_current is not null && _offset < _current.Length)
                {
                    var take = Math.Min(buffer.Length, _current.Length - _offset);
                    _current.AsSpan(_offset, take).CopyTo(buffer.Span);
                    _offset += take;
                    return take;
                }

                if (_frames.TryDequeue(out var frame))
                {
                    _current = frame;
                    _offset = 0;
                    continue;
                }

                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
        }

        public override void Write(byte[] buffer, int offset, int count) { }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.CompletedTask;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
