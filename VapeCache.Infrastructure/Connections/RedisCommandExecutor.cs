using System.Buffers;
using System.Diagnostics;
using VapeCache.Abstractions.Connections;
using Microsoft.Extensions.Options;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisCommandExecutor : IRedisCommandExecutor
{
    private readonly RedisMultiplexedConnection[] _conns;
    private int _rr;
    private readonly bool _instrument;
    private readonly bool _coalesce;
    private (string Key, int ValueLen)[]? _msetLengthsCache;

    public RedisCommandExecutor(
        IRedisConnectionFactory factory,
        IOptions<RedisMultiplexerOptions> options)
    {
        var o = options.Value;
        var count = Math.Max(1, o.Connections);
        _conns = new RedisMultiplexedConnection[count];
        for (var i = 0; i < count; i++)
            _conns[i] = new RedisMultiplexedConnection(factory, o.MaxInFlightPerConnection, o.EnableCoalescedSocketWrites);
        _instrument = o.EnableCommandInstrumentation;
        _coalesce = o.EnableCoalescedSocketWrites;
    }

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("GET");
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);
        var len = RedisRespProtocol.GetGetCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteGetCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            return resp.Kind switch
            {
                RedisRespReader.RespKind.NullBulkString => null,
                RedisRespReader.RespKind.BulkString => resp.Bulk,
                _ => throw new InvalidOperationException($"Unexpected GET response: {resp.Kind}")
            };
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            if (_instrument) RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<RedisValueLease> GetLeaseAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("GET");
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);
        var len = RedisRespProtocol.GetGetCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteGetCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            if (resp.Kind == RedisRespReader.RespKind.NullBulkString)
                return RedisValueLease.Null;
            if (resp.Kind == RedisRespReader.RespKind.BulkString && resp.Bulk is not null)
                return new RedisValueLease(resp.Bulk, resp.BulkLength, pooled: resp.BulkIsPooled);

            RedisRespReader.ReturnBuffers(resp);
            throw new InvalidOperationException($"Unexpected GET response: {resp.Kind}");
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            if (_instrument) RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<byte[]?> GetExAsync(string key, TimeSpan? ttl, CancellationToken ct)
    {
        using var activity = StartCommandActivity("GETEX");
        activity?.SetTag("db.redis.ttl_ms", ttl is null ? null : (long)ttl.Value.TotalMilliseconds);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        int? ttlMs = null;
        if (ttl is not null)
        {
            var ms = (long)ttl.Value.TotalMilliseconds;
            ttlMs = (int)Math.Clamp(ms, 1, int.MaxValue);
        }

        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            var len = RedisRespProtocol.GetGetExCommandLength(key, ttlMs);
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteGetExCommand(rented.AsSpan(0, len), key, ttlMs);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            return resp.Kind switch
            {
                RedisRespReader.RespKind.NullBulkString => null,
                RedisRespReader.RespKind.BulkString => resp.Bulk,
                _ => throw new InvalidOperationException($"Unexpected GETEX response: {resp.Kind}")
            };
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            if (_instrument) RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<RedisValueLease> GetExLeaseAsync(string key, TimeSpan? ttl, CancellationToken ct)
    {
        using var activity = StartCommandActivity("GETEX");
        activity?.SetTag("db.redis.ttl_ms", ttl is null ? null : (long)ttl.Value.TotalMilliseconds);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        int? ttlMs = null;
        if (ttl is not null)
        {
            var ms = (long)ttl.Value.TotalMilliseconds;
            ttlMs = (int)Math.Clamp(ms, 1, int.MaxValue);
        }

        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            var len = RedisRespProtocol.GetGetExCommandLength(key, ttlMs);
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteGetExCommand(rented.AsSpan(0, len), key, ttlMs);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            if (resp.Kind == RedisRespReader.RespKind.NullBulkString)
                return RedisValueLease.Null;
            if (resp.Kind == RedisRespReader.RespKind.BulkString && resp.Bulk is not null)
                return new RedisValueLease(resp.Bulk, resp.BulkLength, pooled: resp.BulkIsPooled);

            RedisRespReader.ReturnBuffers(resp);
            throw new InvalidOperationException($"Unexpected GETEX response: {resp.Kind}");
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            if (_instrument) RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<byte[]?[]> MGetAsync(string[] keys, CancellationToken ct)
    {
        using var activity = StartCommandActivity("MGET");
        activity?.SetTag("db.redis.key_count", keys.Length);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetMGetCommandLength(keys);
        if (len == 0) return Array.Empty<byte[]?>();

        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteMGetCommand(rented.AsSpan(0, len), keys);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
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
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            if (_instrument) RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<bool> SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct)
    {
        using var activity = StartCommandActivity("SET");
        activity?.SetTag("db.redis.ttl_ms", ttl is null ? null : (long)ttl.Value.TotalMilliseconds);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);
        int? ttlMs = null;
        if (ttl is not null)
        {
            var ms = (long)ttl.Value.TotalMilliseconds;
            ttlMs = (int)Math.Clamp(ms, 1, int.MaxValue);
        }

        var len = RedisRespProtocol.GetSetCommandLength(key, value.Length, ttlMs);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            var headerLen = len - value.Length - 2; // exclude value bytes + CRLF
            conn = Next();
            rented = conn.RentHeaderBuffer(headerLen);
            var written = RedisRespProtocol.WriteSetCommandHeader(rented.AsSpan(0, headerLen), key, value.Length, ttlMs);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: value,
                appendCrlf: true,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            return resp.Kind == RedisRespReader.RespKind.SimpleString && ReferenceEquals(resp.Text, RedisRespReader.OkSimpleString);
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            if (_instrument) RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<bool> MSetAsync((string Key, ReadOnlyMemory<byte> Value)[] items, CancellationToken ct)
    {
        using var activity = StartCommandActivity("MSET");
        activity?.SetTag("db.redis.key_count", items.Length);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        if (items.Length == 0) return true;

        var len = RedisRespProtocol.GetMSetCommandLength(items);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();

            var headerLen = len;
            for (var i = 0; i < items.Length; i++)
                headerLen -= items[i].Value.Length + 2; // strip payload + CRLF

            rented = conn.RentHeaderBuffer(headerLen);
            var payloads = conn.RentPayloadArray(items.Length);
            for (var i = 0; i < items.Length; i++)
                payloads[i] = items[i].Value;

            var lengths = RentMSetLengths(items.Length);
            try
            {
                for (var i = 0; i < items.Length; i++)
                    lengths[i] = (items[i].Key, items[i].Value.Length);

                var written = RedisRespProtocol.WriteMSetCommandHeader(rented.AsSpan(0, headerLen), lengths.AsSpan(0, items.Length));
                var resp = await conn.ExecuteAsync(
                    rented.AsMemory(0, written),
                    payload: ReadOnlyMemory<byte>.Empty,
                    appendCrlf: false,
                    poolBulk: false,
                    ct,
                    headerBuffer: rented,
                    payloads: payloads,
                    payloadCount: items.Length,
                    payloadArrayBuffer: payloads).ConfigureAwait(false);

                rented = null; // returned by writer
                return resp.Kind == RedisRespReader.RespKind.SimpleString && string.Equals(resp.Text, "OK", StringComparison.Ordinal);
            }
            finally
            {
                ReturnMSetLengths(lengths, items.Length);
                conn.ReturnPayloadArray(payloads);
            }
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            if (_instrument) RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<bool> DeleteAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("DEL");
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);
        var len = RedisRespProtocol.GetDelCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteDelCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            return resp.Kind == RedisRespReader.RespKind.Integer && resp.IntegerValue > 0;
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            if (_instrument) RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<long> UnlinkAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("UNLINK");
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);
        var len = RedisRespProtocol.GetUnlinkCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteUnlinkCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            return resp.Kind == RedisRespReader.RespKind.Integer ? resp.IntegerValue : 0;
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            if (_instrument) RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<long> TtlSecondsAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("TTL");
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);
        var len = RedisRespProtocol.GetTtlCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteTtlCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            return resp.Kind == RedisRespReader.RespKind.Integer ? resp.IntegerValue : -3;
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            if (_instrument) RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<long> PTtlMillisecondsAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("PTTL");
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);
        var len = RedisRespProtocol.GetPTtlCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WritePTtlCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            return resp.Kind == RedisRespReader.RespKind.Integer ? resp.IntegerValue : -3;
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            if (_instrument) RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<long> HSetAsync(string key, string field, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        using var activity = StartCommandActivity("HSET");
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetHSetCommandLength(key, field, value.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            var headerLen = len - value.Length - 2;
            conn = Next();
            rented = conn.RentHeaderBuffer(headerLen);
            var written = RedisRespProtocol.WriteHSetCommandHeader(rented.AsSpan(0, headerLen), key, field, value.Length);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: value,
                appendCrlf: true,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            return resp.Kind == RedisRespReader.RespKind.Integer ? resp.IntegerValue : 0;
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            if (_instrument) RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<RedisValueLease> HGetLeaseAsync(string key, string field, CancellationToken ct)
    {
        using var activity = StartCommandActivity("HGET");
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetHGetCommandLength(key, field);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteHGetCommand(rented.AsSpan(0, len), key, field);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            if (resp.Kind == RedisRespReader.RespKind.NullBulkString)
                return RedisValueLease.Null;
            if (resp.Kind == RedisRespReader.RespKind.BulkString && resp.Bulk is not null)
                return new RedisValueLease(resp.Bulk, resp.BulkLength, pooled: resp.BulkIsPooled);

        RedisRespReader.ReturnBuffers(resp);
            throw new InvalidOperationException($"Unexpected HGET response: {resp.Kind}");
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            if (_instrument) RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<byte[]?> HGetAsync(string key, string field, CancellationToken ct)
    {
        using var activity = StartCommandActivity("HGET");
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetHGetCommandLength(key, field);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteHGetCommand(rented.AsSpan(0, len), key, field);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            return resp.Kind switch
            {
                RedisRespReader.RespKind.NullBulkString => null,
                RedisRespReader.RespKind.BulkString => resp.Bulk,
                _ => null
            };
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            if (_instrument) RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<byte[]?[]> HMGetAsync(string key, string[] fields, CancellationToken ct)
    {
        using var activity = StartCommandActivity("HMGET");
        activity?.SetTag("db.redis.field_count", fields.Length);
        var sw = Stopwatch.StartNew();
        RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetHMGetCommandLength(key, fields);
        if (len == 0) return Array.Empty<byte[]?>();

        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteHMGetCommand(rented.AsSpan(0, len), key, fields);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            if (resp.Kind is RedisRespReader.RespKind.NullArray)
                return new byte[]?[fields.Length];

            if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null)
                return new byte[]?[fields.Length];

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
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<long> LPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        using var activity = StartCommandActivity("LPUSH");
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetLPushCommandLength(key, value.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            var headerLen = len - value.Length - 2;
            conn = Next();
            rented = conn.RentHeaderBuffer(headerLen);
            var written = RedisRespProtocol.WriteLPushCommandHeader(rented.AsSpan(0, headerLen), key, value.Length);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: value,
                appendCrlf: true,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            return resp.Kind == RedisRespReader.RespKind.Integer ? resp.IntegerValue : 0;
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            if (_instrument) RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<byte[]?> LPopAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("LPOP");
        var sw = Stopwatch.StartNew();
        RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetLPopCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteLPopCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            return resp.Kind switch
            {
                RedisRespReader.RespKind.NullBulkString => null,
                RedisRespReader.RespKind.BulkString => resp.Bulk,
                _ => null
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
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<RedisValueLease> LPopLeaseAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("LPOP");
        var sw = Stopwatch.StartNew();
        RedisTelemetry.CommandCalls.Add(1);
        var len = RedisRespProtocol.GetLPopCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteLPopCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            if (resp.Kind == RedisRespReader.RespKind.NullBulkString)
                return RedisValueLease.Null;
            if (resp.Kind == RedisRespReader.RespKind.BulkString && resp.Bulk is not null)
                return new RedisValueLease(resp.Bulk, resp.BulkLength, pooled: resp.BulkIsPooled);

        RedisRespReader.ReturnBuffers(resp);
            throw new InvalidOperationException($"Unexpected LPOP response: {resp.Kind}");
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
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<byte[]?[]> LRangeAsync(string key, long start, long stop, CancellationToken ct)
    {
        using var activity = StartCommandActivity("LRANGE");
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetLRangeCommandLength(key, start, stop);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteLRangeCommand(rented.AsSpan(0, len), key, start, stop);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            if (resp.Kind is RedisRespReader.RespKind.NullArray || resp.ArrayItems is null)
                return Array.Empty<byte[]?>();

            if (resp.Kind is not RedisRespReader.RespKind.Array)
                return Array.Empty<byte[]?>();

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
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            if (_instrument) RedisTelemetry.CommandMs.Record(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _conns)
            await c.DisposeAsync().ConfigureAwait(false);
    }

    private RedisMultiplexedConnection Next()
    {
        var idx = Interlocked.Increment(ref _rr) & int.MaxValue;
        return _conns[idx % _conns.Length];
    }

    private Activity? StartCommandActivity(string op)
    {
        if (!_instrument) return null;
        var a = RedisTelemetry.ActivitySource.StartActivity("redis.command", ActivityKind.Client);
        if (a is null) return null;
        a.SetTag("db.system", "redis");
        a.SetTag("db.operation", op);
        return a;
    }

    private (string Key, int ValueLen)[] RentMSetLengths(int length)
    {
        var arr = Interlocked.Exchange(ref _msetLengthsCache, null);
        if (arr is null || arr.Length < length)
            arr = new (string Key, int ValueLen)[length];
        return arr;
    }

    private void ReturnMSetLengths((string Key, int ValueLen)[]? lengths, int used)
    {
        if (lengths is null) return;

        // Prevent long-lived retention of key strings.
        used = Math.Clamp(used, 0, lengths.Length);
        for (var i = 0; i < used; i++)
            lengths[i] = default;

        // Avoid caching very large arrays (reduces retention + cache pollution).
        const int MaxCachedLength = 1024;
        if (lengths.Length > MaxCachedLength) return;

        Interlocked.CompareExchange(ref _msetLengthsCache, lengths, null);
    }
}