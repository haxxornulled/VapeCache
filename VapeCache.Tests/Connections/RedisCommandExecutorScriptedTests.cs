using System.Net.Sockets;
using LanguageExt;
using LanguageExt.Common;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.Connections;

public sealed class RedisCommandExecutorScriptedTests
{
    [Fact]
    public async Task HGetAsync_unexpected_shape_resets_transport_and_recovers()
    {
        var stream = new ScriptedResponseStream();
        await using var factory = new ReconnectingConnectionFactory(stream);
        await using var sut = CreateExecutor(factory);

        stream.Enqueue(":1\r\n");
        stream.Enqueue("+PONG\r\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sut.HGetAsync("h1", "f1", default));

        Assert.Contains("Unexpected HGET response", ex.Message);
        Assert.Equal("PONG", await sut.PingAsync(default));

        var lane = Assert.Single(((IRedisMultiplexerDiagnostics)sut).GetMuxLaneSnapshots());
        Assert.True(lane.TransportResets >= 1);
        Assert.True(factory.CreateCount >= 2);
    }

    [Fact]
    public async Task TryHGetAsync_unexpected_shape_resets_transport_and_recovers()
    {
        var stream = new ScriptedResponseStream();
        await using var factory = new ReconnectingConnectionFactory(stream);
        await using var sut = CreateExecutor(factory);

        stream.Enqueue(":1\r\n");
        stream.Enqueue("$2\r\nok\r\n");

        Assert.True(sut.TryHGetAsync("h1", "f1", default, out var task));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await task);

        Assert.Contains("Unexpected HGET response", ex.Message);
        Assert.Equal("ok", System.Text.Encoding.UTF8.GetString((await sut.GetAsync("k1", default))!));

        var lane = Assert.Single(((IRedisMultiplexerDiagnostics)sut).GetMuxLaneSnapshots());
        Assert.True(lane.TransportResets >= 1);
        Assert.True(factory.CreateCount >= 2);
    }

    [Fact]
    public async Task ModuleListAsync_unexpected_shape_resets_transport_and_recovers()
    {
        var stream = new ScriptedResponseStream();
        await using var factory = new ReconnectingConnectionFactory(stream);
        await using var sut = CreateExecutor(factory);

        stream.Enqueue(":1\r\n");
        stream.Enqueue("+PONG\r\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sut.ModuleListAsync(default));

        Assert.Contains("Unexpected MODULE LIST response", ex.Message);
        Assert.Equal("PONG", await sut.PingAsync(default));

        var lane = Assert.Single(((IRedisMultiplexerDiagnostics)sut).GetMuxLaneSnapshots());
        Assert.True(lane.TransportResets >= 1);
        Assert.True(factory.CreateCount >= 2);
    }

    [Fact]
    public async Task TryGetAsync_with_autoscaling_enabled_records_transport_latency_sample()
    {
        var stream = new ScriptedResponseStream();
        await using var factory = new ReconnectingConnectionFactory(stream);
        await using var sut = CreateExecutor(
            factory,
            new RedisMultiplexerOptions
            {
                Connections = 1,
                MaxInFlightPerConnection = 8,
                EnableCoalescedSocketWrites = false,
                EnableCommandInstrumentation = false,
                EnableAutoscaling = true,
                AutoscaleSampleInterval = TimeSpan.FromDays(1),
                ResponseTimeout = TimeSpan.FromSeconds(2)
            });

        stream.Enqueue("$2\r\nok\r\n");

        Assert.True(sut.TryGetAsync("k1", default, out var task));
        Assert.Equal("ok", System.Text.Encoding.UTF8.GetString((await task)!));
        InvokeEvaluateAutoscale(sut);
        Assert.True(sut.GetAutoscalerSnapshot().RollingP95LatencyMs > 0);
    }

    [Fact]
    public async Task Executes_and_parses_core_command_surfaces()
    {
        var stream = new ScriptedResponseStream();
        await using var connection = new ScriptedConnection(stream);
        await using var factory = new SingleConnectionFactory(connection);
        await using var sut = new RedisCommandExecutor(
            factory,
            new TestOptionsMonitor<RedisMultiplexerOptions>(new RedisMultiplexerOptions
            {
                Connections = 1,
                MaxInFlightPerConnection = 8,
                EnableCoalescedSocketWrites = false,
                EnableCommandInstrumentation = false,
                ResponseTimeout = TimeSpan.FromSeconds(2)
            }),
            new TestOptionsMonitor<RedisConnectionOptions>(new RedisConnectionOptions()));

        // Key-value + try wrappers
        stream.Enqueue("+OK\r\n");
        Assert.True(await sut.SetAsync("k1", "v1"u8.ToArray(), null, default));

        stream.Enqueue("$2\r\nv1\r\n");
        Assert.Equal("v1", System.Text.Encoding.UTF8.GetString((await sut.GetAsync("k1", default))!));

        stream.Enqueue("$2\r\nv1\r\n");
        Assert.True(sut.TryGetAsync("k1", default, out var tryGetTask));
        Assert.Equal("v1", System.Text.Encoding.UTF8.GetString((await tryGetTask)!));

        stream.Enqueue("$2\r\nv1\r\n");
        Assert.Equal("v1", System.Text.Encoding.UTF8.GetString((await sut.GetExAsync("k1", TimeSpan.FromSeconds(1), default))!));

        stream.Enqueue("$2\r\nv1\r\n");
        Assert.True(sut.TryGetExAsync("k1", TimeSpan.FromSeconds(1), default, out var tryGetExTask));
        Assert.Equal("v1", System.Text.Encoding.UTF8.GetString((await tryGetExTask)!));

        stream.Enqueue("+OK\r\n");
        Assert.True(await sut.MSetAsync(new[] { ("k2", (ReadOnlyMemory<byte>)"v2"u8.ToArray()) }, default));

        stream.Enqueue("*2\r\n$2\r\nv1\r\n$-1\r\n");
        var mget = await sut.MGetAsync(new[] { "k1", "missing" }, default);
        Assert.Equal(2, mget.Length);
        Assert.NotNull(mget[0]);
        Assert.Null(mget[1]);

        stream.Enqueue("+OK\r\n");
        Assert.True(sut.TrySetAsync("k3", "v3"u8.ToArray(), null, default, out var trySetTask));
        Assert.True(await trySetTask);

        stream.Enqueue(":1\r\n");
        Assert.True(await sut.DeleteAsync("k1", default));

        stream.Enqueue(":10\r\n");
        Assert.Equal(10, await sut.TtlSecondsAsync("k1", default));

        stream.Enqueue(":100\r\n");
        Assert.Equal(100, await sut.PTtlMillisecondsAsync("k1", default));

        stream.Enqueue(":1\r\n");
        Assert.Equal(1, await sut.UnlinkAsync("k1", default));

        // Lease methods + try wrappers
        stream.Enqueue("$1\r\nz\r\n");
        using (var lease = await sut.GetLeaseAsync("kz", default))
        {
            Assert.False(lease.IsNull);
        }

        stream.Enqueue("$1\r\nz\r\n");
        Assert.True(sut.TryGetLeaseAsync("kz", default, out var tryLeaseTask));
        using (var lease = await tryLeaseTask)
        {
            Assert.False(lease.IsNull);
        }

        stream.Enqueue("$1\r\nz\r\n");
        using (var lease = await sut.GetExLeaseAsync("kz", TimeSpan.FromSeconds(1), default))
        {
            Assert.False(lease.IsNull);
        }

        stream.Enqueue("$1\r\nz\r\n");
        Assert.True(sut.TryGetExLeaseAsync("kz", TimeSpan.FromSeconds(1), default, out var tryExLeaseTask));
        using (var lease = await tryExLeaseTask)
        {
            Assert.False(lease.IsNull);
        }

        // Hash
        stream.Enqueue(":1\r\n");
        Assert.Equal(1, await sut.HSetAsync("h1", "f1", "x"u8.ToArray(), default));

        stream.Enqueue("$1\r\nx\r\n");
        Assert.Equal("x", System.Text.Encoding.UTF8.GetString((await sut.HGetAsync("h1", "f1", default))!));

        stream.Enqueue("$1\r\nx\r\n");
        Assert.True(sut.TryHGetAsync("h1", "f1", default, out var tryHGetTask));
        Assert.Equal("x", System.Text.Encoding.UTF8.GetString((await tryHGetTask)!));

        stream.Enqueue("*2\r\n$1\r\nx\r\n$-1\r\n");
        var hmget = await sut.HMGetAsync("h1", new[] { "f1", "f2" }, default);
        Assert.Equal(2, hmget.Length);

        stream.Enqueue("$1\r\nx\r\n");
        using (var lease = await sut.HGetLeaseAsync("h1", "f1", default))
        {
            Assert.False(lease.IsNull);
        }

        // Lists + try wrappers
        stream.Enqueue(":1\r\n");
        Assert.Equal(1, await sut.LPushAsync("l1", "a"u8.ToArray(), default));

        stream.Enqueue(":2\r\n");
        Assert.Equal(2, await sut.RPushAsync("l1", "b"u8.ToArray(), default));

        stream.Enqueue("$1\r\na\r\n");
        Assert.Equal("a", System.Text.Encoding.UTF8.GetString((await sut.LPopAsync("l1", default))!));

        stream.Enqueue("$1\r\na\r\n");
        Assert.True(sut.TryLPopAsync("l1", default, out var tryLPopTask));
        Assert.Equal("a", System.Text.Encoding.UTF8.GetString((await tryLPopTask)!));

        stream.Enqueue("$1\r\nb\r\n");
        Assert.Equal("b", System.Text.Encoding.UTF8.GetString((await sut.RPopAsync("l1", default))!));

        stream.Enqueue("$1\r\nb\r\n");
        Assert.True(sut.TryRPopAsync("l1", default, out var tryRPopTask));
        Assert.Equal("b", System.Text.Encoding.UTF8.GetString((await tryRPopTask)!));

        stream.Enqueue("*2\r\n$1\r\na\r\n$1\r\nb\r\n");
        Assert.Equal(2, (await sut.LRangeAsync("l1", 0, -1, default)).Length);

        stream.Enqueue(":2\r\n");
        Assert.Equal(2, await sut.LLenAsync("l1", default));

        stream.Enqueue("$1\r\na\r\n");
        using (var lease = await sut.LPopLeaseAsync("l1", default)) { Assert.False(lease.IsNull); }

        stream.Enqueue("$1\r\na\r\n");
        Assert.True(sut.TryLPopLeaseAsync("l1", default, out var tryLPopLeaseTask));
        using (var lease = await tryLPopLeaseTask) { Assert.False(lease.IsNull); }

        stream.Enqueue("$1\r\nb\r\n");
        using (var lease = await sut.RPopLeaseAsync("l1", default)) { Assert.False(lease.IsNull); }

        stream.Enqueue("$1\r\nb\r\n");
        Assert.True(sut.TryRPopLeaseAsync("l1", default, out var tryRPopLeaseTask));
        using (var lease = await tryRPopLeaseTask) { Assert.False(lease.IsNull); }

        // Sets
        stream.Enqueue(":1\r\n");
        Assert.Equal(1, await sut.SAddAsync("s1", "m1"u8.ToArray(), default));

        stream.Enqueue(":1\r\n");
        Assert.Equal(1, await sut.SRemAsync("s1", "m1"u8.ToArray(), default));

        stream.Enqueue(":1\r\n");
        Assert.True(await sut.SIsMemberAsync("s1", "m1"u8.ToArray(), default));

        stream.Enqueue(":1\r\n");
        Assert.True(sut.TrySIsMemberAsync("s1", "m1"u8.ToArray(), default, out var tryIsMemberTask));
        Assert.True(await tryIsMemberTask);

        stream.Enqueue("*1\r\n$2\r\nm1\r\n");
        Assert.Single(await sut.SMembersAsync("s1", default));

        stream.Enqueue(":1\r\n");
        Assert.Equal(1, await sut.SCardAsync("s1", default));

        // Sorted sets
        stream.Enqueue(":1\r\n");
        Assert.Equal(1, await sut.ZAddAsync("z1", 1.5, "m1"u8.ToArray(), default));

        stream.Enqueue(":1\r\n");
        Assert.Equal(1, await sut.ZRemAsync("z1", "m1"u8.ToArray(), default));

        stream.Enqueue(":1\r\n");
        Assert.Equal(1, await sut.ZCardAsync("z1", default));

        stream.Enqueue("$3\r\n1.5\r\n");
        Assert.Equal(1.5, await sut.ZScoreAsync("z1", "m1"u8.ToArray(), default));

        stream.Enqueue(":0\r\n");
        Assert.Equal(0, await sut.ZRankAsync("z1", "m1"u8.ToArray(), false, default));

        stream.Enqueue("$3\r\n2.5\r\n");
        Assert.Equal(2.5, await sut.ZIncrByAsync("z1", 1, "m1"u8.ToArray(), default));

        stream.Enqueue("*2\r\n$2\r\nm1\r\n$3\r\n2.5\r\n");
        Assert.Single(await sut.ZRangeWithScoresAsync("z1", 0, -1, false, default));

        stream.Enqueue("*2\r\n$2\r\nm1\r\n$3\r\n2.5\r\n");
        Assert.Single(await sut.ZRangeByScoreWithScoresAsync("z1", 0, 10, false, null, null, default));

        // JSON
        stream.Enqueue("$7\r\n{\"a\":1}\r\n");
        Assert.NotNull(await sut.JsonGetAsync("j1", ".", default));

        stream.Enqueue("$7\r\n{\"a\":1}\r\n");
        using (var lease = await sut.JsonGetLeaseAsync("j1", ".", default)) { Assert.False(lease.IsNull); }

        stream.Enqueue("$7\r\n{\"a\":1}\r\n");
        Assert.True(sut.TryJsonGetLeaseAsync("j1", ".", default, out var tryJsonLeaseTask));
        using (var lease = await tryJsonLeaseTask) { Assert.False(lease.IsNull); }

        stream.Enqueue("+OK\r\n");
        Assert.True(await sut.JsonSetAsync("j1", ".", "{\"a\":1}"u8.ToArray(), default));

        stream.Enqueue("$7\r\n{\"a\":1}\r\n");
        stream.Enqueue("+OK\r\n");
        using (var payloadLease = await sut.JsonGetLeaseAsync("j1", ".", default))
        {
            Assert.True(await sut.JsonSetLeaseAsync("j1", ".", payloadLease, default));
        }

        stream.Enqueue(":1\r\n");
        Assert.Equal(1, await sut.JsonDelAsync("j1", ".", default));

        // Modules/time series/search
        stream.Enqueue("+OK\r\n");
        Assert.True(await sut.FtCreateAsync("idx", "doc:", new[] { "title" }, default));

        stream.Enqueue("*0\r\n");
        Assert.Empty(await sut.FtSearchAsync("idx", "*", null, null, default));

        stream.Enqueue(":1\r\n");
        Assert.True(await sut.BfAddAsync("bf1", "x"u8.ToArray(), default));

        stream.Enqueue(":1\r\n");
        Assert.True(await sut.BfExistsAsync("bf1", "x"u8.ToArray(), default));

        stream.Enqueue("+OK\r\n");
        Assert.True(await sut.TsCreateAsync("ts1", default));

        stream.Enqueue(":100\r\n");
        Assert.Equal(100, await sut.TsAddAsync("ts1", 100, 1.0, default));

        stream.Enqueue("*1\r\n*2\r\n:100\r\n$1\r\n1\r\n");
        Assert.Single(await sut.TsRangeAsync("ts1", 0, 1000, default));

        // Server + stream + extended fallback-only methods
        stream.Enqueue("+PONG\r\n");
        Assert.Equal("PONG", await sut.PingAsync(default));

        stream.Enqueue("*0\r\n");
        Assert.Empty(await sut.ModuleListAsync(default));
    }

    [Fact]
    public async Task FtSearchAsync_parses_resp2_document_ids_with_payload_rows()
    {
        var stream = new ScriptedResponseStream();
        await using var factory = new ReconnectingConnectionFactory(stream);
        await using var sut = CreateExecutor(factory);

        stream.Enqueue("*3\r\n:1\r\n$9\r\npos:sku:1\r\n*2\r\n$3\r\nsku\r\n$1\r\n1\r\n");

        var ids = await sut.FtSearchAsync("idx:pos", "pencil*", 0, 10, default);

        Assert.Single(ids);
        Assert.Equal("pos:sku:1", ids[0]);
    }

    [Fact]
    public async Task FtSearchAsync_parses_resp3_map_results_with_id_fields()
    {
        var stream = new ScriptedResponseStream();
        await using var factory = new ReconnectingConnectionFactory(stream);
        await using var sut = CreateExecutor(factory);

        stream.Enqueue(
            "%2\r\n" +
            "+total_results\r\n" +
            ":1\r\n" +
            "+results\r\n" +
            "*1\r\n" +
            "%2\r\n" +
            "+id\r\n" +
            "$9\r\npos:sku:2\r\n" +
            "+extra_attributes\r\n" +
            "%1\r\n" +
            "+name\r\n" +
            "$6\r\nPencil\r\n");

        var ids = await sut.FtSearchAsync("idx:pos", "*", 0, 10, default);

        Assert.Single(ids);
        Assert.Equal("pos:sku:2", ids[0]);
    }

    [Fact]
    public async Task FtCreateAsync_returns_false_when_index_already_exists()
    {
        var stream = new ScriptedResponseStream();
        await using var factory = new ReconnectingConnectionFactory(stream);
        await using var sut = CreateExecutor(factory);

        stream.Enqueue("-Index already exists\r\n");

        var created = await sut.FtCreateAsync("idx:pos", "pos:sku:", new[] { "name", "code" }, default);

        Assert.False(created);
    }

    private static RedisCommandExecutor CreateExecutor(
        IRedisConnectionFactory factory,
        RedisMultiplexerOptions? options = null)
        => new(
            factory,
            new TestOptionsMonitor<RedisMultiplexerOptions>(options ?? new RedisMultiplexerOptions
            {
                Connections = 1,
                MaxInFlightPerConnection = 8,
                EnableCoalescedSocketWrites = false,
                EnableCommandInstrumentation = false,
                ResponseTimeout = TimeSpan.FromSeconds(2)
            }),
            new TestOptionsMonitor<RedisConnectionOptions>(new RedisConnectionOptions()));

    private static void InvokeEvaluateAutoscale(RedisCommandExecutor executor)
    {
        var method = typeof(RedisCommandExecutor).GetMethod(
            "EvaluateAutoscale",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(executor, null);
    }

    private sealed class SingleConnectionFactory(ScriptedConnection connection) : IRedisConnectionFactory
    {
        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
            => ValueTask.FromResult(new Result<IRedisConnection>(connection));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ReconnectingConnectionFactory(ScriptedResponseStream stream) : IRedisConnectionFactory
    {
        private int _createCount;

        public int CreateCount => Volatile.Read(ref _createCount);

        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _createCount);
            return ValueTask.FromResult(new Result<IRedisConnection>(new ScriptedConnection(stream)));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
        private readonly Queue<byte[]> _frames = new();
        private byte[]? _current;
        private int _offset;
        private readonly object _gate = new();

        public void Enqueue(string frame)
            => Enqueue(System.Text.Encoding.UTF8.GetBytes(frame));

        public void Enqueue(byte[] frame)
        {
            lock (_gate)
            {
                _frames.Enqueue(frame);
            }
        }

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

                lock (_gate)
                {
                    if (_current is not null && _offset < _current.Length)
                    {
                        var take = Math.Min(buffer.Length, _current.Length - _offset);
                        _current.AsSpan(_offset, take).CopyTo(buffer.Span);
                        _offset += take;
                        return take;
                    }

                    if (_frames.Count > 0)
                    {
                        _current = _frames.Dequeue();
                        _offset = 0;
                        continue;
                    }
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
