using System.Buffers;
using System.Diagnostics;
using VapeCache.Abstractions.Connections;
using Microsoft.Extensions.Options;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisCommandExecutor : IRedisCommandExecutor
{
    private readonly RedisMultiplexedConnection[] _conns;
    private int _rr;

    public RedisCommandExecutor(
        IRedisConnectionFactory factory,
        IOptions<RedisMultiplexerOptions> options)
    {
        var o = options.Value;
        var count = Math.Max(1, o.Connections);
        _conns = new RedisMultiplexedConnection[count];
        for (var i = 0; i < count; i++)
            _conns[i] = new RedisMultiplexedConnection(factory, o.MaxInFlightPerConnection);
    }

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("GET");
        var sw = Stopwatch.StartNew();
        RedisTelemetry.CommandCalls.Add(1);
        var len = RedisRespProtocol.GetGetCommandLength(key);
        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(len);
            var written = RedisRespProtocol.WriteGetCommand(rented.AsSpan(0, len), key);
            var resp = await Next().ExecuteAsync(rented.AsMemory(0, written), ct).ConfigureAwait(false);
            return resp.Kind switch
            {
                RedisRespReader.RespKind.NullBulkString => null,
                RedisRespReader.RespKind.BulkString => resp.Bulk,
                _ => throw new InvalidOperationException($"Unexpected GET response: {resp.Kind}")
            };
        }
        catch
        {
            RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async ValueTask<byte[]?> GetExAsync(string key, TimeSpan? ttl, CancellationToken ct)
    {
        using var activity = StartCommandActivity("GETEX");
        activity?.SetTag("db.redis.ttl_ms", ttl is null ? null : (long)ttl.Value.TotalMilliseconds);
        var sw = Stopwatch.StartNew();
        RedisTelemetry.CommandCalls.Add(1);

        int? ttlMs = null;
        if (ttl is not null)
        {
            var ms = (long)ttl.Value.TotalMilliseconds;
            ttlMs = (int)Math.Clamp(ms, 1, int.MaxValue);
        }

        byte[]? rented = null;
        try
        {
            var len = RedisRespProtocol.GetGetExCommandLength(key, ttlMs);
            rented = ArrayPool<byte>.Shared.Rent(len);
            var written = RedisRespProtocol.WriteGetExCommand(rented.AsSpan(0, len), key, ttlMs);
            var resp = await Next().ExecuteAsync(rented.AsMemory(0, written), ct).ConfigureAwait(false);
            return resp.Kind switch
            {
                RedisRespReader.RespKind.NullBulkString => null,
                RedisRespReader.RespKind.BulkString => resp.Bulk,
                _ => throw new InvalidOperationException($"Unexpected GETEX response: {resp.Kind}")
            };
        }
        catch
        {
            RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async ValueTask<byte[]?[]> MGetAsync(string[] keys, CancellationToken ct)
    {
        using var activity = StartCommandActivity("MGET");
        activity?.SetTag("db.redis.key_count", keys.Length);
        var sw = Stopwatch.StartNew();
        RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetMGetCommandLength(keys);
        if (len == 0) return Array.Empty<byte[]?>();

        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(len);
            var written = RedisRespProtocol.WriteMGetCommand(rented.AsSpan(0, len), keys);
            var resp = await Next().ExecuteAsync(rented.AsMemory(0, written), ct).ConfigureAwait(false);
            if (resp.Kind is RedisRespReader.RespKind.NullArray)
                return new byte[]?[keys.Length];

            if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null)
                throw new InvalidOperationException($"Unexpected MGET response: {resp.Kind}");

            var items = resp.ArrayItems;
            var result = new byte[]?[items.Length];
            for (var i = 0; i < items.Length; i++)
            {
                result[i] = items[i].Kind switch
                {
                    RedisRespReader.RespKind.NullBulkString => null,
                    RedisRespReader.RespKind.BulkString => items[i].Bulk,
                    _ => null
                };
            }
            return result;
        }
        catch
        {
            RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async ValueTask<bool> SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct)
    {
        using var activity = StartCommandActivity("SET");
        activity?.SetTag("db.redis.ttl_ms", ttl is null ? null : (long)ttl.Value.TotalMilliseconds);
        var sw = Stopwatch.StartNew();
        RedisTelemetry.CommandCalls.Add(1);
        int? ttlMs = null;
        if (ttl is not null)
        {
            var ms = (long)ttl.Value.TotalMilliseconds;
            ttlMs = (int)Math.Clamp(ms, 1, int.MaxValue);
        }

        var len = RedisRespProtocol.GetSetCommandLength(key, value.Length, ttlMs);
        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(len);
            var written = RedisRespProtocol.WriteSetCommand(rented.AsSpan(0, len), key, value.Span, ttlMs);
            var resp = await Next().ExecuteAsync(rented.AsMemory(0, written), ct).ConfigureAwait(false);
            return resp.Kind == RedisRespReader.RespKind.SimpleString && string.Equals(resp.Text, "OK", StringComparison.Ordinal);
        }
        catch
        {
            RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async ValueTask<bool> MSetAsync((string Key, ReadOnlyMemory<byte> Value)[] items, CancellationToken ct)
    {
        using var activity = StartCommandActivity("MSET");
        activity?.SetTag("db.redis.key_count", items.Length);
        var sw = Stopwatch.StartNew();
        RedisTelemetry.CommandCalls.Add(1);

        if (items.Length == 0) return true;

        var len = RedisRespProtocol.GetMSetCommandLength(items);
        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(len);
            var written = RedisRespProtocol.WriteMSetCommand(rented.AsSpan(0, len), items);
            var resp = await Next().ExecuteAsync(rented.AsMemory(0, written), ct).ConfigureAwait(false);
            return resp.Kind == RedisRespReader.RespKind.SimpleString && string.Equals(resp.Text, "OK", StringComparison.Ordinal);
        }
        catch
        {
            RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async ValueTask<bool> DeleteAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("DEL");
        var sw = Stopwatch.StartNew();
        RedisTelemetry.CommandCalls.Add(1);
        var len = RedisRespProtocol.GetDelCommandLength(key);
        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(len);
            var written = RedisRespProtocol.WriteDelCommand(rented.AsSpan(0, len), key);
            var resp = await Next().ExecuteAsync(rented.AsMemory(0, written), ct).ConfigureAwait(false);
            return resp.Kind == RedisRespReader.RespKind.Integer && resp.IntegerValue > 0;
        }
        catch
        {
            RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async ValueTask<long> UnlinkAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("UNLINK");
        var sw = Stopwatch.StartNew();
        RedisTelemetry.CommandCalls.Add(1);
        var len = RedisRespProtocol.GetUnlinkCommandLength(key);
        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(len);
            var written = RedisRespProtocol.WriteUnlinkCommand(rented.AsSpan(0, len), key);
            var resp = await Next().ExecuteAsync(rented.AsMemory(0, written), ct).ConfigureAwait(false);
            return resp.Kind == RedisRespReader.RespKind.Integer ? resp.IntegerValue : 0;
        }
        catch
        {
            RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async ValueTask<long> TtlSecondsAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("TTL");
        var sw = Stopwatch.StartNew();
        RedisTelemetry.CommandCalls.Add(1);
        var len = RedisRespProtocol.GetTtlCommandLength(key);
        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(len);
            var written = RedisRespProtocol.WriteTtlCommand(rented.AsSpan(0, len), key);
            var resp = await Next().ExecuteAsync(rented.AsMemory(0, written), ct).ConfigureAwait(false);
            return resp.Kind == RedisRespReader.RespKind.Integer ? resp.IntegerValue : -3;
        }
        catch
        {
            RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async ValueTask<long> PTtlMillisecondsAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("PTTL");
        var sw = Stopwatch.StartNew();
        RedisTelemetry.CommandCalls.Add(1);
        var len = RedisRespProtocol.GetPTtlCommandLength(key);
        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(len);
            var written = RedisRespProtocol.WritePTtlCommand(rented.AsSpan(0, len), key);
            var resp = await Next().ExecuteAsync(rented.AsMemory(0, written), ct).ConfigureAwait(false);
            return resp.Kind == RedisRespReader.RespKind.Integer ? resp.IntegerValue : -3;
        }
        catch
        {
            RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _conns)
            await c.DisposeAsync().ConfigureAwait(false);
    }

    private RedisMultiplexedConnection Next()
    {
        var idx = Interlocked.Increment(ref _rr);
        return _conns[idx % _conns.Length];
    }

    private static Activity? StartCommandActivity(string op)
    {
        var a = RedisTelemetry.ActivitySource.StartActivity("redis.command", ActivityKind.Client);
        if (a is null) return null;
        a.SetTag("db.system", "redis");
        a.SetTag("db.operation", op);
        return a;
    }
}
