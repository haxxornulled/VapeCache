using BenchmarkDotNet.Attributes;
using LanguageExt.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VapeCache.Application.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

[MemoryDiagnoser]
public class RedisConnectionPoolBenchmarks
{
    private RedisConnectionPool? _pool;

    [GlobalSetup]
    public void Setup()
    {
        var o = new RedisConnectionOptions
        {
            Host = "127.0.0.1",
            Port = 6379,
            MaxConnections = 1024,
            MaxIdle = 1024,
            Warm = 0,
            AcquireTimeout = TimeSpan.FromSeconds(1),
            ConnectTimeout = TimeSpan.FromSeconds(1),
            ValidateAfterIdle = TimeSpan.Zero,
            ReaperPeriod = TimeSpan.Zero
        };

        var monitor = new SimpleOptionsMonitor(o);
        var factory = new FakeFactory();
        _pool = new RedisConnectionPool(factory, monitor, NullLogger<RedisConnectionPool>.Instance);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_pool is not null)
            await _pool.DisposeAsync().ConfigureAwait(false);
    }

    [Benchmark]
    public async ValueTask RentReturn()
    {
        var r = await _pool!.RentAsync(CancellationToken.None).ConfigureAwait(false);
        await r.Match(async lease => await lease.DisposeAsync().ConfigureAwait(false), ex => throw ex).ConfigureAwait(false);
    }

    private sealed class SimpleOptionsMonitor : IOptionsMonitor<RedisConnectionOptions>
    {
        public SimpleOptionsMonitor(RedisConnectionOptions current) => CurrentValue = current;
        public RedisConnectionOptions CurrentValue { get; }
        public RedisConnectionOptions Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<RedisConnectionOptions, string?> listener) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class FakeFactory : IRedisConnectionFactory
    {
        private long _id;
        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _id);
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            var stream = Stream.Null;
            return ValueTask.FromResult(new Result<IRedisConnection>(new FakeConn(id, socket, stream)));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class FakeConn(long id, System.Net.Sockets.Socket socket, Stream stream) : IRedisConnection
        {
            public System.Net.Sockets.Socket Socket => socket;
            public Stream Stream => stream;
            public ValueTask<Result<LanguageExt.Unit>> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct) => ValueTask.FromResult<Result<LanguageExt.Unit>>(LanguageExt.Prelude.unit);
            public ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct) => ValueTask.FromResult<Result<int>>(0);
            public ValueTask DisposeAsync()
            {
                try { socket.Dispose(); } catch { }
                return ValueTask.CompletedTask;
            }
        }
    }
}
