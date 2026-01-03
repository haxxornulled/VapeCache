using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.Text;
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

    // Backwards compatibility constructor - delegates to IOptions<T> monitor
    public RedisCommandExecutor(
        IRedisConnectionFactory factory,
        IOptions<RedisMultiplexerOptions> options)
        : this(factory, new OptionsMonitorWrapper<RedisMultiplexerOptions>(options.Value), null)
    {
    }

    public RedisCommandExecutor(
        IRedisConnectionFactory factory,
        IOptionsMonitor<RedisMultiplexerOptions> options,
        IOptionsMonitor<RedisConnectionOptions>? connectionOptions = null)
    {
        var o = options.CurrentValue;
        var connOpts = connectionOptions?.CurrentValue ?? new RedisConnectionOptions();
        var count = Math.Max(1, o.Connections);
        _conns = new RedisMultiplexedConnection[count];
        for (var i = 0; i < count; i++)
            _conns[i] = new RedisMultiplexedConnection(
                factory,
                o.MaxInFlightPerConnection,
                o.EnableCoalescedSocketWrites,
                connOpts.MaxBulkStringBytes,
                connOpts.MaxArrayDepth,
                o.ResponseTimeout);
        _instrument = o.EnableCommandInstrumentation;
        _coalesce = o.EnableCoalescedSocketWrites;
    }

    // Simple wrapper to convert IOptions to IOptionsMonitor
    private class OptionsMonitorWrapper<T> : IOptionsMonitor<T>
    {
        private readonly T _value;
        public OptionsMonitorWrapper(T value) => _value = value;
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    public IRedisBatch CreateBatch()
        => new RedisBatch(this);

    private static string FormatDouble(double value)
    {
        if (double.IsNaN(value))
            throw new ArgumentOutOfRangeException(nameof(value), "NaN is not a valid Redis score.");
        if (double.IsPositiveInfinity(value))
            return "+inf";
        if (double.IsNegativeInfinity(value))
            return "-inf";
        return value.ToString("G17", CultureInfo.InvariantCulture);
    }

    private static int GetBulkLength(RedisRespReader.RespValue value)
    {
        if (value.Bulk is null)
            return 0;
        if (value.BulkLength > 0)
            return value.BulkLength;
        return value.Bulk.Length;
    }

    private static long ParseCursor(RedisRespReader.RespValue value)
    {
        if (value.Kind is not RedisRespReader.RespKind.BulkString)
            throw new InvalidOperationException($"Unexpected SCAN cursor kind: {value.Kind}");
        var length = GetBulkLength(value);
        var span = (value.Bulk ?? Array.Empty<byte>()).AsSpan(0, length);
        if (Utf8Parser.TryParse(span, out long cursor, out var consumed) && consumed == length)
            return cursor;
        var text = Encoding.UTF8.GetString(span);
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out cursor))
            throw new InvalidOperationException($"Invalid SCAN cursor: {text}");
        return cursor;
    }

    private static double ParseDouble(RedisRespReader.RespValue value)
    {
        string text;
        if (value.Kind == RedisRespReader.RespKind.SimpleString)
        {
            text = value.Text ?? throw new InvalidOperationException("Unexpected score value: empty simple string.");
        }
        else if (value.Kind == RedisRespReader.RespKind.BulkString)
        {
            var length = GetBulkLength(value);
            var span = (value.Bulk ?? Array.Empty<byte>()).AsSpan(0, length);
            if (Utf8Parser.TryParse(span, out double parsed, out var consumed) && consumed == length)
                return parsed;
            text = Encoding.UTF8.GetString(span);
        }
        else
        {
            throw new InvalidOperationException($"Unexpected score kind: {value.Kind}");
        }

        if (string.Equals(text, "+inf", StringComparison.OrdinalIgnoreCase))
            return double.PositiveInfinity;
        if (string.Equals(text, "-inf", StringComparison.OrdinalIgnoreCase))
            return double.NegativeInfinity;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
            throw new InvalidOperationException($"Invalid score value: {text}");
        return score;
    }

    private static long ParseLong(RedisRespReader.RespValue value)
    {
        if (value.Kind == RedisRespReader.RespKind.Integer)
            return value.IntegerValue;
        if (value.Kind != RedisRespReader.RespKind.BulkString)
            throw new InvalidOperationException($"Unexpected integer kind: {value.Kind}");

        var length = GetBulkLength(value);
        var span = (value.Bulk ?? Array.Empty<byte>()).AsSpan(0, length);
        if (Utf8Parser.TryParse(span, out long parsed, out var consumed) && consumed == length)
            return parsed;
        var text = Encoding.UTF8.GetString(span);
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            throw new InvalidOperationException($"Invalid integer value: {text}");
        return parsed;
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
                RedisRespReader.RespKind.BulkString => resp.BulkIsPooled
                    ? resp.Bulk.AsSpan(0, resp.BulkLength).ToArray()
                    : resp.Bulk,
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

    public bool TryGetAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);
        var len = RedisRespProtocol.GetGetCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteGetCommand(rented.AsSpan(0, len), key);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapGetResponseAsync(respTask);
            return true;
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
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

    public bool TryGetLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);
        var len = RedisRespProtocol.GetGetCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteGetCommand(rented.AsSpan(0, len), key);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapLeaseResponseAsync(respTask, "GET");
            return true;
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
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
                RedisRespReader.RespKind.BulkString => resp.BulkIsPooled
                    ? resp.Bulk.AsSpan(0, resp.BulkLength).ToArray()
                    : resp.Bulk,
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

    public bool TryGetExAsync(string key, TimeSpan? ttl, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);
        int? ttlMs = null;
        if (ttl is not null)
        {
            var ms = (long)ttl.Value.TotalMilliseconds;
            ttlMs = (int)Math.Clamp(ms, 1, int.MaxValue);
        }

        var len = RedisRespProtocol.GetGetExCommandLength(key, ttlMs);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteGetExCommand(rented.AsSpan(0, len), key, ttlMs);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapGetResponseAsync(respTask);
            return true;
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
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

    public async ValueTask<byte[]?> GetRangeAsync(string key, long start, long end, CancellationToken ct)
    {
        using var activity = StartCommandActivity("GETRANGE");
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);
        var len = RedisRespProtocol.GetGetRangeCommandLength(key, start, end);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteGetRangeCommand(rented.AsSpan(0, len), key, start, end);
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
                RedisRespReader.RespKind.BulkString => resp.BulkIsPooled
                    ? resp.Bulk.AsSpan(0, resp.BulkLength).ToArray()
                    : resp.Bulk,
                _ => throw new InvalidOperationException($"Unexpected GETRANGE response: {resp.Kind}")
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
            try
            {
                if (resp.Kind is RedisRespReader.RespKind.NullArray)
                    return new byte[]?[keys.Length];

                if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null)
                    throw new InvalidOperationException($"Unexpected MGET response: {resp.Kind}");

                var items = resp.ArrayItems;
                var count = resp.ArrayLength;
                var result = new byte[]?[count];
                for (var i = 0; i < count; i++)
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
            finally
            {
                RedisRespReader.ReturnBuffers(resp);
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

        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();

            // For SET without TTL, use scatter/gather I/O for zero-copy performance
            if (ttlMs is null)
            {
                var len = RedisRespProtocol.GetSetCommandLength(key, value.Length, null);
                var headerLen = len - value.Length - 2; // exclude value bytes + CRLF
                rented = conn.RentHeaderBuffer(headerLen);
                var written = RedisRespProtocol.WriteSetCommandHeader(rented.AsSpan(0, headerLen), key, value.Length, null);
                var resp = await conn.ExecuteAsync(
                    rented.AsMemory(0, written),
                    payload: value,
                    appendCrlf: true,
                    poolBulk: false,
                    ct,
                    headerBuffer: rented).ConfigureAwait(false);
                rented = null; // returned by writer

                if (resp.Kind == RedisRespReader.RespKind.Error)
                    throw new InvalidOperationException($"Redis error: {resp.Text}");

                // Use string equality instead of reference equality for more reliable checking
                return resp.Kind == RedisRespReader.RespKind.SimpleString &&
                       (ReferenceEquals(resp.Text, RedisRespReader.OkSimpleString) || resp.Text == "OK");
            }
            else
            {
                // For SET with TTL (SET key value PX ttl), we must build the entire command
                // because the command structure is: [header][value][suffix] where suffix = PX ttl
                // Scatter/gather I/O doesn't support a suffix after payload+CRLF
                var len = RedisRespProtocol.GetSetCommandLength(key, value.Length, ttlMs);
                rented = ArrayPool<byte>.Shared.Rent(len);
                var written = RedisRespProtocol.WriteSetCommand(rented.AsSpan(0, len), key, value.Span, ttlMs);
                var resp = await conn.ExecuteAsync(
                    rented.AsMemory(0, written),
                    poolBulk: false,
                    ct).ConfigureAwait(false);

                if (resp.Kind == RedisRespReader.RespKind.Error)
                    throw new InvalidOperationException($"Redis error: {resp.Text}");

                // Use string equality instead of reference equality for more reliable checking
                return resp.Kind == RedisRespReader.RespKind.SimpleString &&
                       (ReferenceEquals(resp.Text, RedisRespReader.OkSimpleString) || resp.Text == "OK");
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
            if (rented is not null)
            {
                if (ttlMs is not null)
                    ArrayPool<byte>.Shared.Return(rented);
                else if (conn is not null)
                    conn.ReturnHeaderBuffer(rented);
            }
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
            rented = ArrayPool<byte>.Shared.Rent(len);
            var written = RedisRespProtocol.WriteMSetCommand(rented.AsSpan(0, len), items);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                poolBulk: false,
                ct).ConfigureAwait(false);
            return resp.Kind == RedisRespReader.RespKind.SimpleString && string.Equals(resp.Text, "OK", StringComparison.Ordinal);
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
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
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
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

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
            try
            {
                if (resp.Kind is RedisRespReader.RespKind.NullArray)
                    return new byte[]?[fields.Length];

                if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null)
                    throw new InvalidOperationException($"RESP protocol violation: Expected Array for HMGET, got {resp.Kind}. Possible corrupted response stream.");

                var items = resp.ArrayItems;
                var count = resp.ArrayLength;
                var result = new byte[]?[count];
                for (var i = 0; i < count; i++)
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
            finally
            {
                RedisRespReader.ReturnBuffers(resp);
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
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

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

    public async ValueTask<bool> ExpireAsync(string key, TimeSpan ttl, CancellationToken ct)
    {
        using var activity = StartCommandActivity("EXPIRE");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var seconds = (long)Math.Ceiling(ttl.TotalSeconds);
        seconds = Math.Clamp(seconds, 0L, int.MaxValue);
        var secondsInt = (int)seconds;

        var len = RedisRespProtocol.GetExpireCommandLength(key, secondsInt);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteExpireCommand(rented.AsSpan(0, len), key, secondsInt);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer

            return resp.Kind == RedisRespReader.RespKind.Integer && resp.IntegerValue == 1;
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

    public bool TryHGetAsync(string key, string field, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);
        var len = RedisRespProtocol.GetHGetCommandLength(key, field);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteHGetCommand(rented.AsSpan(0, len), key, field);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapGetResponseAsync(respTask);
            return true;
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public bool TrySetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct, out ValueTask<bool> task)
    {
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
            conn = Next();

            if (ttlMs is null)
            {
                var len = RedisRespProtocol.GetSetCommandLength(key, value.Length, null);
                var headerLen = len - value.Length - 2;
                rented = conn.RentHeaderBuffer(headerLen);
                var written = RedisRespProtocol.WriteSetCommandHeader(rented.AsSpan(0, headerLen), key, value.Length, null);
                if (!conn.TryExecuteAsync(
                    rented.AsMemory(0, written),
                    payload: value,
                    appendCrlf: true,
                    poolBulk: false,
                    ct,
                    out var respTask,
                    headerBuffer: rented))
                {
                    task = default;
                    return false;
                }

                rented = null; // returned by writer
                task = MapSetResponseAsync(respTask);
                return true;
            }

            var fullLen = RedisRespProtocol.GetSetCommandLength(key, value.Length, ttlMs);
            rented = ArrayPool<byte>.Shared.Rent(fullLen);
            var fullWritten = RedisRespProtocol.WriteSetCommand(rented.AsSpan(0, fullLen), key, value.Span, ttlMs);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, fullWritten),
                poolBulk: false,
                ct,
                out var respTaskTtl))
            {
                task = default;
                return false;
            }

            var buffer = rented;
            rented = null;
            task = MapSetResponseAsync(respTaskTtl, buffer);
            return true;
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            if (rented is not null)
            {
                if (ttlMs is not null)
                    ArrayPool<byte>.Shared.Return(rented);
                else if (conn is not null)
                    conn.ReturnHeaderBuffer(rented);
            }
        }
    }

    public bool TryGetExLeaseAsync(string key, TimeSpan? ttl, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);
        int? ttlMs = null;
        if (ttl is not null)
        {
            var ms = (long)ttl.Value.TotalMilliseconds;
            ttlMs = (int)Math.Clamp(ms, 1, int.MaxValue);
        }

        var len = RedisRespProtocol.GetGetExCommandLength(key, ttlMs);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteGetExCommand(rented.AsSpan(0, len), key, ttlMs);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapLeaseResponseAsync(respTask, "GETEX");
            return true;
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public bool TryLPopAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetLPopCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteLPopCommand(rented.AsSpan(0, len), key);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapLPopResponseAsync(respTask);
            return true;
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<RedisValueLease> LPopLeaseAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("LPOP");
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);
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

    public async ValueTask<byte[]?> LIndexAsync(string key, long index, CancellationToken ct)
    {
        using var activity = StartCommandActivity("LINDEX");
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);
        var len = RedisRespProtocol.GetLIndexCommandLength(key, index);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteLIndexCommand(rented.AsSpan(0, len), key, index);
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
                RedisRespReader.RespKind.BulkString => resp.BulkIsPooled
                    ? resp.Bulk.AsSpan(0, resp.BulkLength).ToArray()
                    : resp.Bulk,
                _ => throw new InvalidOperationException($"Unexpected LINDEX response: {resp.Kind}")
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
            try
            {
                if (resp.Kind is RedisRespReader.RespKind.NullArray)
                    return Array.Empty<byte[]?>();

                if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null)
                    throw new InvalidOperationException($"RESP protocol violation: Expected Array for LRANGE, got {resp.Kind}. Possible corrupted response stream.");

                var items = resp.ArrayItems;
                var count = resp.ArrayLength;
                var result = new byte[]?[count];
                for (var i = 0; i < count; i++)
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
            finally
            {
                RedisRespReader.ReturnBuffers(resp);
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

    public async ValueTask DisposeAsync()
    {
        _msetLengthsCache = null;

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
        if (!RedisTelemetry.ActivitySource.HasListeners()) return null;
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

        used = Math.Clamp(used, 0, lengths.Length);
        for (var i = 0; i < used; i++)
            lengths[i] = default;

        const int MaxCachedLength = 1024;
        if (lengths.Length > MaxCachedLength) return;

        Interlocked.CompareExchange(ref _msetLengthsCache, lengths, null);
    }

    private static ValueTask<byte[]?> MapGetResponseAsync(ValueTask<RedisRespReader.RespValue> respTask)
    {
        if (respTask.IsCompletedSuccessfully)
        {
            try
            {
                return new ValueTask<byte[]?>(MapGetResponse(respTask.Result));
            }
            catch (Exception ex)
            {
                return new ValueTask<byte[]?>(Task.FromException<byte[]?>(ex));
            }
        }

        return AwaitMapGetResponseAsync(respTask);

        static async ValueTask<byte[]?> AwaitMapGetResponseAsync(ValueTask<RedisRespReader.RespValue> task)
        {
            var resp = await task.ConfigureAwait(false);
            return MapGetResponse(resp);
        }

        static byte[]? MapGetResponse(RedisRespReader.RespValue resp)
        {
            return resp.Kind switch
            {
                RedisRespReader.RespKind.NullBulkString => null,
                RedisRespReader.RespKind.BulkString => resp.Bulk,
                RedisRespReader.RespKind.Error => throw new InvalidOperationException($"Redis error: {resp.Text}"),
                _ => throw new InvalidOperationException($"Unexpected GET response: {resp.Kind}")
            };
        }
    }

    private static ValueTask<RedisValueLease> MapLeaseResponseAsync(ValueTask<RedisRespReader.RespValue> respTask, string op)
    {
        if (respTask.IsCompletedSuccessfully)
            return new ValueTask<RedisValueLease>(MapLeaseResponse(respTask.Result, op));

        return AwaitMapLeaseResponseAsync(respTask, op);

        static async ValueTask<RedisValueLease> AwaitMapLeaseResponseAsync(ValueTask<RedisRespReader.RespValue> task, string op)
        {
            var resp = await task.ConfigureAwait(false);
            return MapLeaseResponse(resp, op);
        }

        static RedisValueLease MapLeaseResponse(RedisRespReader.RespValue resp, string op)
        {
            if (resp.Kind == RedisRespReader.RespKind.NullBulkString)
                return RedisValueLease.Null;
            if (resp.Kind == RedisRespReader.RespKind.BulkString && resp.Bulk is not null)
                return new RedisValueLease(resp.Bulk, resp.BulkLength, pooled: resp.BulkIsPooled);

            RedisRespReader.ReturnBuffers(resp);
            throw new InvalidOperationException($"Unexpected {op} response: {resp.Kind}");
        }
    }

    private static ValueTask<bool> MapSetResponseAsync(ValueTask<RedisRespReader.RespValue> respTask, byte[]? rented = null)
    {
        if (respTask.IsCompletedSuccessfully)
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
            return new ValueTask<bool>(MapSetResponse(respTask.Result));
        }

        return AwaitMapSetResponseAsync(respTask, rented);

        static async ValueTask<bool> AwaitMapSetResponseAsync(ValueTask<RedisRespReader.RespValue> task, byte[]? rented)
        {
            try
            {
                var resp = await task.ConfigureAwait(false);
                return MapSetResponse(resp);
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }

        static bool MapSetResponse(RedisRespReader.RespValue resp)
        {
            if (resp.Kind == RedisRespReader.RespKind.Error)
                throw new InvalidOperationException($"Redis error: {resp.Text}");

            return resp.Kind == RedisRespReader.RespKind.SimpleString &&
                   (ReferenceEquals(resp.Text, RedisRespReader.OkSimpleString) || resp.Text == "OK");
        }
    }

    private static ValueTask<bool> MapSIsMemberResponseAsync(ValueTask<RedisRespReader.RespValue> respTask)
    {
        if (respTask.IsCompletedSuccessfully)
            return new ValueTask<bool>(MapSIsMemberResponse(respTask.Result));

        return AwaitMapSIsMemberResponseAsync(respTask);

        static async ValueTask<bool> AwaitMapSIsMemberResponseAsync(ValueTask<RedisRespReader.RespValue> task)
        {
            var resp = await task.ConfigureAwait(false);
            return MapSIsMemberResponse(resp);
        }

        static bool MapSIsMemberResponse(RedisRespReader.RespValue resp)
            => resp.Kind == RedisRespReader.RespKind.Integer && resp.IntegerValue == 1;
    }

    private static ValueTask<byte[]?> MapLPopResponseAsync(ValueTask<RedisRespReader.RespValue> respTask)
    {
        if (respTask.IsCompletedSuccessfully)
            return new ValueTask<byte[]?>(MapLPopResponse(respTask.Result));

        return AwaitMapLPopResponseAsync(respTask);

        static async ValueTask<byte[]?> AwaitMapLPopResponseAsync(ValueTask<RedisRespReader.RespValue> task)
        {
            var resp = await task.ConfigureAwait(false);
            return MapLPopResponse(resp);
        }

        static byte[]? MapLPopResponse(RedisRespReader.RespValue resp)
        {
            return resp.Kind switch
            {
                RedisRespReader.RespKind.NullBulkString => null,
                RedisRespReader.RespKind.BulkString => resp.Bulk,
                _ => null
            };
        }
    }

    public bool TryLPopLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);
        var len = RedisRespProtocol.GetLPopCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteLPopCommand(rented.AsSpan(0, len), key);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapLeaseResponseAsync(respTask, "LPOP");
            return true;
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    private static ValueTask<byte[]?> MapRPopResponseAsync(ValueTask<RedisRespReader.RespValue> respTask)
    {
        if (respTask.IsCompletedSuccessfully)
            return new ValueTask<byte[]?>(MapRPopResponse(respTask.Result));

        return AwaitMapRPopResponseAsync(respTask);

        static async ValueTask<byte[]?> AwaitMapRPopResponseAsync(ValueTask<RedisRespReader.RespValue> task)
        {
            var resp = await task.ConfigureAwait(false);
            return MapRPopResponse(resp);
        }

        static byte[]? MapRPopResponse(RedisRespReader.RespValue resp)
        {
            return resp.Kind switch
            {
                RedisRespReader.RespKind.BulkString => resp.BulkIsPooled
                    ? resp.Bulk.AsSpan(0, resp.BulkLength).ToArray()
                    : resp.Bulk,
                RedisRespReader.RespKind.NullBulkString => null,
                _ => null
            };
        }
    }

    // ========== List Commands ==========

    public async ValueTask<long> RPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        using var activity = StartCommandActivity("RPUSH");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetRPushCommandLength(key, value.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteRPushCommand(rented.AsSpan(0, len), key, value.Span);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer

            return resp.Kind == RedisRespReader.RespKind.Integer
                ? resp.IntegerValue
                : throw new InvalidOperationException($"Unexpected RPUSH response: {resp.Kind}");
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

    public async ValueTask<byte[]?> RPopAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("RPOP");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetRPopCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteRPopCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer

            return resp.Kind switch
            {
                RedisRespReader.RespKind.BulkString => resp.BulkIsPooled
                    ? resp.Bulk.AsSpan(0, resp.BulkLength).ToArray()
                    : resp.Bulk,
                RedisRespReader.RespKind.NullBulkString => null,
                _ => throw new InvalidOperationException($"Unexpected RPOP response: {resp.Kind}")
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

    public bool TryRPopAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetRPopCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteRPopCommand(rented.AsSpan(0, len), key);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapRPopResponseAsync(respTask);
            return true;
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<RedisValueLease> RPopLeaseAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("RPOP");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetRPopCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteRPopCommand(rented.AsSpan(0, len), key);
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
            throw new InvalidOperationException($"Unexpected RPOP response: {resp.Kind}");
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

    public bool TryRPopLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);
        var len = RedisRespProtocol.GetRPopCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteRPopCommand(rented.AsSpan(0, len), key);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapLeaseResponseAsync(respTask, "RPOP");
            return true;
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<long> LLenAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("LLEN");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetLLenCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteLLenCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer

            return resp.Kind == RedisRespReader.RespKind.Integer
                ? resp.IntegerValue
                : throw new InvalidOperationException($"Unexpected LLEN response: {resp.Kind}");
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

    // ========== Set Commands ==========

    public async ValueTask<long> SAddAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        using var activity = StartCommandActivity("SADD");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetSAddCommandLength(key, member.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteSAddCommand(rented.AsSpan(0, len), key, member.Span);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer

            return resp.Kind == RedisRespReader.RespKind.Integer
                ? resp.IntegerValue
                : throw new InvalidOperationException($"Unexpected SADD response: {resp.Kind}");
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

    public async ValueTask<long> SRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        using var activity = StartCommandActivity("SREM");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetSRemCommandLength(key, member.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteSRemCommand(rented.AsSpan(0, len), key, member.Span);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer

            return resp.Kind == RedisRespReader.RespKind.Integer
                ? resp.IntegerValue
                : throw new InvalidOperationException($"Unexpected SREM response: {resp.Kind}");
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

    public async ValueTask<bool> SIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        using var activity = StartCommandActivity("SISMEMBER");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetSIsMemberCommandLength(key, member.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteSIsMemberCommand(rented.AsSpan(0, len), key, member.Span);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer

            return resp.Kind == RedisRespReader.RespKind.Integer && resp.IntegerValue == 1;
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

    public bool TrySIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct, out ValueTask<bool> task)
    {
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);
        var len = RedisRespProtocol.GetSIsMemberCommandLength(key, member.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteSIsMemberCommand(rented.AsSpan(0, len), key, member.Span);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapSIsMemberResponseAsync(respTask);
            return true;
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<byte[]?[]> SMembersAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("SMEMBERS");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetSMembersCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteSMembersCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer

            try
            {
                if (resp.Kind is RedisRespReader.RespKind.NullArray)
                    return Array.Empty<byte[]?>();

                if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null)
                    throw new InvalidOperationException($"Unexpected SMEMBERS response: {resp.Kind}");

                var items = resp.ArrayItems;
                var count = resp.ArrayLength;
                var result = new byte[]?[count];
                for (var i = 0; i < count; i++)
                {
                    result[i] = items[i].Kind switch
                    {
                        RedisRespReader.RespKind.BulkString => items[i].Bulk,
                        RedisRespReader.RespKind.NullBulkString => null,
                        _ => throw new InvalidOperationException($"Unexpected SMEMBERS item kind: {items[i].Kind}")
                    };
                }
                return result;
            }
            finally
            {
                RedisRespReader.ReturnBuffers(resp);
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

    public async ValueTask<long> SCardAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("SCARD");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetSCardCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteSCardCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer

            return resp.Kind == RedisRespReader.RespKind.Integer
                ? resp.IntegerValue
                : throw new InvalidOperationException($"Unexpected SCARD response: {resp.Kind}");
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

    // ========== Sorted Set Commands ==========

    public async ValueTask<long> ZAddAsync(string key, double score, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        using var activity = StartCommandActivity("ZADD");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var scoreText = FormatDouble(score);
        var len = RedisRespProtocol.GetZAddCommandLength(key, scoreText, member.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteZAddCommand(rented.AsSpan(0, len), key, scoreText, member.Span);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return resp.Kind == RedisRespReader.RespKind.Integer
                ? resp.IntegerValue
                : throw new InvalidOperationException($"Unexpected ZADD response: {resp.Kind}");
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

    public async ValueTask<long> ZRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        using var activity = StartCommandActivity("ZREM");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetZRemCommandLength(key, member.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteZRemCommand(rented.AsSpan(0, len), key, member.Span);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return resp.Kind == RedisRespReader.RespKind.Integer
                ? resp.IntegerValue
                : throw new InvalidOperationException($"Unexpected ZREM response: {resp.Kind}");
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

    public async ValueTask<long> ZCardAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("ZCARD");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetZCardCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteZCardCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return resp.Kind == RedisRespReader.RespKind.Integer
                ? resp.IntegerValue
                : throw new InvalidOperationException($"Unexpected ZCARD response: {resp.Kind}");
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

    public async ValueTask<double?> ZScoreAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        using var activity = StartCommandActivity("ZSCORE");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetZScoreCommandLength(key, member.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteZScoreCommand(rented.AsSpan(0, len), key, member.Span);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return resp.Kind switch
            {
                RedisRespReader.RespKind.NullBulkString => null,
                RedisRespReader.RespKind.BulkString => ParseDouble(resp),
                _ => throw new InvalidOperationException($"Unexpected ZSCORE response: {resp.Kind}")
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

    public async ValueTask<long?> ZRankAsync(string key, ReadOnlyMemory<byte> member, bool descending, CancellationToken ct)
    {
        using var activity = StartCommandActivity(descending ? "ZREVRANK" : "ZRANK");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetZRankCommandLength(key, member.Length, descending);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteZRankCommand(rented.AsSpan(0, len), key, member.Span, descending);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return resp.Kind switch
            {
                RedisRespReader.RespKind.NullBulkString => null,
                RedisRespReader.RespKind.Integer => resp.IntegerValue,
                _ => throw new InvalidOperationException($"Unexpected ZRANK response: {resp.Kind}")
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

    public async ValueTask<double> ZIncrByAsync(string key, double increment, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        using var activity = StartCommandActivity("ZINCRBY");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var incrementText = FormatDouble(increment);
        var len = RedisRespProtocol.GetZIncrByCommandLength(key, incrementText, member.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteZIncrByCommand(rented.AsSpan(0, len), key, incrementText, member.Span);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return resp.Kind == RedisRespReader.RespKind.BulkString
                ? ParseDouble(resp)
                : throw new InvalidOperationException($"Unexpected ZINCRBY response: {resp.Kind}");
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

    public async ValueTask<(byte[] Member, double Score)[]> ZRangeWithScoresAsync(string key, long start, long stop, bool descending, CancellationToken ct)
    {
        using var activity = StartCommandActivity(descending ? "ZREVRANGE" : "ZRANGE");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetZRangeWithScoresCommandLength(key, start, stop, descending);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteZRangeWithScoresCommand(rented.AsSpan(0, len), key, start, stop, descending);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            try
            {
                if (resp.Kind is RedisRespReader.RespKind.NullArray)
                    return Array.Empty<(byte[] Member, double Score)>();

                if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null)
                    throw new InvalidOperationException($"Unexpected ZRANGE response: {resp.Kind}");

                var items = resp.ArrayItems;
                var count = resp.ArrayLength;
                if (count == 0)
                    return Array.Empty<(byte[] Member, double Score)>();

                if (count % 2 != 0)
                    throw new InvalidOperationException("ZRANGE WITHSCORES returned an odd number of items.");

                var result = new (byte[] Member, double Score)[count / 2];
                var idx = 0;
                for (var i = 0; i < count; i += 2)
                {
                    if (items[i].Kind is not RedisRespReader.RespKind.BulkString)
                        throw new InvalidOperationException($"Unexpected ZRANGE member kind: {items[i].Kind}");

                    var member = items[i].Bulk ?? Array.Empty<byte>();
                    var score = ParseDouble(items[i + 1]);
                    result[idx++] = (member, score);
                }

                return result;
            }
            finally
            {
                RedisRespReader.ReturnBuffers(resp);
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

    public async ValueTask<(byte[] Member, double Score)[]> ZRangeByScoreWithScoresAsync(
        string key,
        double min,
        double max,
        bool descending,
        long? offset,
        long? count,
        CancellationToken ct)
    {
        using var activity = StartCommandActivity(descending ? "ZREVRANGEBYSCORE" : "ZRANGEBYSCORE");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var minText = FormatDouble(min);
        var maxText = FormatDouble(max);
        var len = RedisRespProtocol.GetZRangeByScoreWithScoresCommandLength(key, minText, maxText, descending, offset, count);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteZRangeByScoreWithScoresCommand(rented.AsSpan(0, len), key, minText, maxText, descending, offset, count);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            try
            {
                if (resp.Kind is RedisRespReader.RespKind.NullArray)
                    return Array.Empty<(byte[] Member, double Score)>();

                if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null)
                    throw new InvalidOperationException($"Unexpected ZRANGEBYSCORE response: {resp.Kind}");

                var items = resp.ArrayItems;
                var itemCount = resp.ArrayLength;
                if (itemCount == 0)
                    return Array.Empty<(byte[] Member, double Score)>();

                if (itemCount % 2 != 0)
                    throw new InvalidOperationException("ZRANGEBYSCORE WITHSCORES returned an odd number of items.");

                var result = new (byte[] Member, double Score)[itemCount / 2];
                var idx = 0;
                for (var i = 0; i < itemCount; i += 2)
                {
                    if (items[i].Kind is not RedisRespReader.RespKind.BulkString)
                        throw new InvalidOperationException($"Unexpected ZRANGEBYSCORE member kind: {items[i].Kind}");

                    var member = items[i].Bulk ?? Array.Empty<byte>();
                    var score = ParseDouble(items[i + 1]);
                    result[idx++] = (member, score);
                }

                return result;
            }
            finally
            {
                RedisRespReader.ReturnBuffers(resp);
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

    // ========== JSON Commands ==========

    public async ValueTask<byte[]?> JsonGetAsync(string key, string? path, CancellationToken ct)
    {
        using var activity = StartCommandActivity("JSON.GET");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetJsonGetCommandLength(key, path);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteJsonGetCommand(rented.AsSpan(0, len), key, path);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return resp.Kind switch
            {
                RedisRespReader.RespKind.NullBulkString => null,
                RedisRespReader.RespKind.BulkString => resp.BulkIsPooled
                    ? resp.Bulk.AsSpan(0, resp.BulkLength).ToArray()
                    : resp.Bulk,
                _ => throw new InvalidOperationException($"Unexpected JSON.GET response: {resp.Kind}")
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

    public async ValueTask<RedisValueLease> JsonGetLeaseAsync(string key, string? path, CancellationToken ct)
    {
        using var activity = StartCommandActivity("JSON.GET");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetJsonGetCommandLength(key, path);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteJsonGetCommand(rented.AsSpan(0, len), key, path);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            if (resp.Kind == RedisRespReader.RespKind.NullBulkString)
                return RedisValueLease.Null;
            if (resp.Kind == RedisRespReader.RespKind.BulkString && resp.Bulk is not null)
                return new RedisValueLease(resp.Bulk, resp.BulkLength, pooled: resp.BulkIsPooled);

            RedisRespReader.ReturnBuffers(resp);
            throw new InvalidOperationException($"Unexpected JSON.GET response: {resp.Kind}");
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

    public bool TryJsonGetLeaseAsync(string key, string? path, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);
        var len = RedisRespProtocol.GetJsonGetCommandLength(key, path);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteJsonGetCommand(rented.AsSpan(0, len), key, path);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null;
            task = MapLeaseResponseAsync(respTask, "JSON.GET");
            return true;
        }
        catch
        {
            if (_instrument) RedisTelemetry.CommandFailures.Add(1);
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<bool> JsonSetAsync(string key, string? path, ReadOnlyMemory<byte> json, CancellationToken ct)
    {
        using var activity = StartCommandActivity("JSON.SET");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        path ??= ".";
        var len = RedisRespProtocol.GetJsonSetCommandLength(key, path, json.Length);
        var headerLen = len - json.Length - 2;
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(headerLen);
            var written = RedisRespProtocol.WriteJsonSetCommandHeader(rented.AsSpan(0, headerLen), key, path, json.Length);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: json,
                appendCrlf: true,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return resp.Kind switch
            {
                RedisRespReader.RespKind.SimpleString => true,
                RedisRespReader.RespKind.Error => throw new InvalidOperationException($"Redis error: {resp.Text}"),
                _ => throw new InvalidOperationException($"Unexpected JSON.SET response: {resp.Kind}")
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

    public ValueTask<bool> JsonSetLeaseAsync(string key, string? path, RedisValueLease json, CancellationToken ct)
        => JsonSetAsync(key, path, json.Memory, ct);

    public async ValueTask<long> JsonDelAsync(string key, string? path, CancellationToken ct)
    {
        using var activity = StartCommandActivity("JSON.DEL");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetJsonDelCommandLength(key, path);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteJsonDelCommand(rented.AsSpan(0, len), key, path);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return resp.Kind == RedisRespReader.RespKind.Integer
                ? resp.IntegerValue
                : throw new InvalidOperationException($"Unexpected JSON.DEL response: {resp.Kind}");
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

    // ========== RediSearch / RedisBloom / RedisTimeSeries ==========

    public async ValueTask<bool> FtCreateAsync(string index, string prefix, string[] fields, CancellationToken ct)
    {
        using var activity = StartCommandActivity("FT.CREATE");
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetFtCreateCommandLength(index, prefix, fields);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteFtCreateCommand(rented.AsSpan(0, len), index, prefix, fields);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return resp.Kind == RedisRespReader.RespKind.SimpleString;
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

    public async ValueTask<string[]> FtSearchAsync(string index, string query, int? offset, int? count, CancellationToken ct)
    {
        using var activity = StartCommandActivity("FT.SEARCH");
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetFtSearchCommandLength(index, query, offset, count);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteFtSearchCommand(rented.AsSpan(0, len), index, query, offset, count);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            try
            {
                if (resp.Kind is RedisRespReader.RespKind.NullArray)
                    return Array.Empty<string>();

                if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null)
                    throw new InvalidOperationException($"Unexpected FT.SEARCH response: {resp.Kind}");

                var items = resp.ArrayItems;
                var itemCount = resp.ArrayLength;
                if (itemCount <= 1)
                    return Array.Empty<string>();

                var idCount = 0;
                for (var i = 1; i < itemCount; i++)
                {
                    if (items[i].Kind == RedisRespReader.RespKind.BulkString)
                        idCount++;
                }

                if (idCount == 0)
                    return Array.Empty<string>();

                var ids = new string[idCount];
                var idx = 0;
                for (var i = 1; i < itemCount; i++)
                {
                    if (items[i].Kind == RedisRespReader.RespKind.BulkString)
                        ids[idx++] = Encoding.UTF8.GetString(items[i].Bulk ?? Array.Empty<byte>(), 0, GetBulkLength(items[i]));
                }

                return ids;
            }
            finally
            {
                RedisRespReader.ReturnBuffers(resp);
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

    public async ValueTask<bool> BfAddAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct)
    {
        using var activity = StartCommandActivity("BF.ADD");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetBfAddCommandLength(key, item.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteBfAddCommand(rented.AsSpan(0, len), key, item.Span);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return resp.Kind == RedisRespReader.RespKind.Integer && resp.IntegerValue == 1;
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

    public async ValueTask<bool> BfExistsAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct)
    {
        using var activity = StartCommandActivity("BF.EXISTS");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetBfExistsCommandLength(key, item.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteBfExistsCommand(rented.AsSpan(0, len), key, item.Span);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return resp.Kind == RedisRespReader.RespKind.Integer && resp.IntegerValue == 1;
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

    public async ValueTask<bool> TsCreateAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("TS.CREATE");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetTsCreateCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteTsCreateCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return resp.Kind == RedisRespReader.RespKind.SimpleString;
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

    public async ValueTask<long> TsAddAsync(string key, long timestamp, double value, CancellationToken ct)
    {
        using var activity = StartCommandActivity("TS.ADD");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var valueText = FormatDouble(value);
        var len = RedisRespProtocol.GetTsAddCommandLength(key, timestamp, valueText);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteTsAddCommand(rented.AsSpan(0, len), key, timestamp, valueText);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return resp.Kind == RedisRespReader.RespKind.Integer
                ? resp.IntegerValue
                : throw new InvalidOperationException($"Unexpected TS.ADD response: {resp.Kind}");
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

    public async ValueTask<(long Timestamp, double Value)[]> TsRangeAsync(string key, long from, long to, CancellationToken ct)
    {
        using var activity = StartCommandActivity("TS.RANGE");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetTsRangeCommandLength(key, from, to);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteTsRangeCommand(rented.AsSpan(0, len), key, from, to);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            try
            {
                if (resp.Kind is RedisRespReader.RespKind.NullArray)
                    return Array.Empty<(long Timestamp, double Value)>();

                if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null)
                    throw new InvalidOperationException($"Unexpected TS.RANGE response: {resp.Kind}");

                var items = resp.ArrayItems;
                var count = resp.ArrayLength;
                if (count == 0)
                    return Array.Empty<(long Timestamp, double Value)>();

                var result = new (long Timestamp, double Value)[count];
                for (var i = 0; i < count; i++)
                {
                    var entry = items[i];
                    var entryItems = entry.ArrayItems;
                    var entryCount = entry.ArrayLength;
                    if (entry.Kind is not RedisRespReader.RespKind.Array || entryItems is null || entryCount < 2)
                        throw new InvalidOperationException($"Unexpected TS.RANGE sample kind: {entry.Kind}");

                    var timestamp = ParseLong(entryItems[0]);
                    var value = ParseDouble(entryItems[1]);
                    result[i] = (timestamp, value);
                }

                return result;
            }
            finally
            {
                RedisRespReader.ReturnBuffers(resp);
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

    // ========== Scan Commands ==========

    public async IAsyncEnumerable<string> ScanAsync(
        string? pattern = null,
        int pageSize = 128,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        long cursor = 0;
        do
        {
            var (nextCursor, keys) = await ScanKeysPageAsync(cursor, pattern, pageSize, ct).ConfigureAwait(false);
            cursor = nextCursor;
            foreach (var key in keys)
                yield return key;
        } while (cursor != 0);
    }

    public async IAsyncEnumerable<byte[]> SScanAsync(
        string key,
        string? pattern = null,
        int pageSize = 128,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        long cursor = 0;
        do
        {
            var (nextCursor, items) = await ScanBytesPageAsync("SSCAN", key, cursor, pattern, pageSize, ct).ConfigureAwait(false);
            cursor = nextCursor;
            foreach (var item in items)
                yield return item;
        } while (cursor != 0);
    }

    public async IAsyncEnumerable<(string Field, byte[] Value)> HScanAsync(
        string key,
        string? pattern = null,
        int pageSize = 128,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        long cursor = 0;
        do
        {
            var (nextCursor, items) = await ScanHashPageAsync(key, cursor, pattern, pageSize, ct).ConfigureAwait(false);
            cursor = nextCursor;
            foreach (var item in items)
                yield return item;
        } while (cursor != 0);
    }

    public async IAsyncEnumerable<(byte[] Member, double Score)> ZScanAsync(
        string key,
        string? pattern = null,
        int pageSize = 128,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        long cursor = 0;
        do
        {
            var (nextCursor, items) = await ScanSortedSetPageAsync(key, cursor, pattern, pageSize, ct).ConfigureAwait(false);
            cursor = nextCursor;
            foreach (var item in items)
                yield return item;
        } while (cursor != 0);
    }

    private async ValueTask<RedisRespReader.RespValue> ExecuteScanPageAsync(
        string command,
        string? key,
        long cursor,
        string? pattern,
        int count,
        CancellationToken ct)
    {
        using var activity = StartCommandActivity(command);
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        var len = RedisRespProtocol.GetScanCommandLength(command, key, cursor, pattern, count);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteScanCommand(rented.AsSpan(0, len), command, key, cursor, pattern, count);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;
            return resp;
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

    private async ValueTask<(long Cursor, string[] Keys)> ScanKeysPageAsync(
        long cursor,
        string? pattern,
        int count,
        CancellationToken ct)
    {
        var resp = await ExecuteScanPageAsync("SCAN", null, cursor, pattern, count, ct).ConfigureAwait(false);
        try
        {
            if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null || resp.ArrayLength < 2)
                throw new InvalidOperationException($"Unexpected SCAN response: {resp.Kind}");

            var cursorValue = ParseCursor(resp.ArrayItems[0]);
            var itemsValue = resp.ArrayItems[1];
            if (itemsValue.Kind is RedisRespReader.RespKind.NullArray)
                return (cursorValue, Array.Empty<string>());

            if (itemsValue.Kind is not RedisRespReader.RespKind.Array || itemsValue.ArrayItems is null)
                throw new InvalidOperationException($"Unexpected SCAN items kind: {itemsValue.Kind}");

            var items = itemsValue.ArrayItems;
            var itemCount = itemsValue.ArrayLength;
            var keys = new string[itemCount];
            for (var i = 0; i < itemCount; i++)
            {
                if (items[i].Kind is not RedisRespReader.RespKind.BulkString)
                    throw new InvalidOperationException($"Unexpected SCAN item kind: {items[i].Kind}");
                keys[i] = Encoding.UTF8.GetString(items[i].Bulk ?? Array.Empty<byte>(), 0, GetBulkLength(items[i]));
            }

            return (cursorValue, keys);
        }
        finally
        {
            RedisRespReader.ReturnBuffers(resp);
        }
    }

    private async ValueTask<(long Cursor, byte[][] Items)> ScanBytesPageAsync(
        string command,
        string key,
        long cursor,
        string? pattern,
        int count,
        CancellationToken ct)
    {
        var resp = await ExecuteScanPageAsync(command, key, cursor, pattern, count, ct).ConfigureAwait(false);
        try
        {
            if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null || resp.ArrayLength < 2)
                throw new InvalidOperationException($"Unexpected {command} response: {resp.Kind}");

            var cursorValue = ParseCursor(resp.ArrayItems[0]);
            var itemsValue = resp.ArrayItems[1];
            if (itemsValue.Kind is RedisRespReader.RespKind.NullArray)
                return (cursorValue, Array.Empty<byte[]>());

            if (itemsValue.Kind is not RedisRespReader.RespKind.Array || itemsValue.ArrayItems is null)
                throw new InvalidOperationException($"Unexpected {command} items kind: {itemsValue.Kind}");

            var items = itemsValue.ArrayItems;
            var itemCount = itemsValue.ArrayLength;
            var result = new byte[itemCount][];
            for (var i = 0; i < itemCount; i++)
            {
                if (items[i].Kind is not RedisRespReader.RespKind.BulkString)
                    throw new InvalidOperationException($"Unexpected {command} item kind: {items[i].Kind}");
                result[i] = items[i].Bulk ?? Array.Empty<byte>();
            }

            return (cursorValue, result);
        }
        finally
        {
            RedisRespReader.ReturnBuffers(resp);
        }
    }

    private async ValueTask<(long Cursor, (string Field, byte[] Value)[] Items)> ScanHashPageAsync(
        string key,
        long cursor,
        string? pattern,
        int count,
        CancellationToken ct)
    {
        var resp = await ExecuteScanPageAsync("HSCAN", key, cursor, pattern, count, ct).ConfigureAwait(false);
        try
        {
            if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null || resp.ArrayLength < 2)
                throw new InvalidOperationException($"Unexpected HSCAN response: {resp.Kind}");

            var cursorValue = ParseCursor(resp.ArrayItems[0]);
            var itemsValue = resp.ArrayItems[1];
            if (itemsValue.Kind is RedisRespReader.RespKind.NullArray)
                return (cursorValue, Array.Empty<(string Field, byte[] Value)>());

            if (itemsValue.Kind is not RedisRespReader.RespKind.Array || itemsValue.ArrayItems is null)
                throw new InvalidOperationException($"Unexpected HSCAN items kind: {itemsValue.Kind}");

            var items = itemsValue.ArrayItems;
            var itemCount = itemsValue.ArrayLength;
            if (itemCount % 2 != 0)
                throw new InvalidOperationException("HSCAN returned an odd number of items.");

            var result = new (string Field, byte[] Value)[itemCount / 2];
            var idx = 0;
            for (var i = 0; i < itemCount; i += 2)
            {
                if (items[i].Kind is not RedisRespReader.RespKind.BulkString)
                    throw new InvalidOperationException($"Unexpected HSCAN field kind: {items[i].Kind}");
                if (items[i + 1].Kind is not RedisRespReader.RespKind.BulkString)
                    throw new InvalidOperationException($"Unexpected HSCAN value kind: {items[i + 1].Kind}");

                var field = Encoding.UTF8.GetString(items[i].Bulk ?? Array.Empty<byte>(), 0, GetBulkLength(items[i]));
                var value = items[i + 1].Bulk ?? Array.Empty<byte>();
                result[idx++] = (field, value);
            }

            return (cursorValue, result);
        }
        finally
        {
            RedisRespReader.ReturnBuffers(resp);
        }
    }

    private async ValueTask<(long Cursor, (byte[] Member, double Score)[] Items)> ScanSortedSetPageAsync(
        string key,
        long cursor,
        string? pattern,
        int count,
        CancellationToken ct)
    {
        var resp = await ExecuteScanPageAsync("ZSCAN", key, cursor, pattern, count, ct).ConfigureAwait(false);
        try
        {
            if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null || resp.ArrayLength < 2)
                throw new InvalidOperationException($"Unexpected ZSCAN response: {resp.Kind}");

            var cursorValue = ParseCursor(resp.ArrayItems[0]);
            var itemsValue = resp.ArrayItems[1];
            if (itemsValue.Kind is RedisRespReader.RespKind.NullArray)
                return (cursorValue, Array.Empty<(byte[] Member, double Score)>());

            if (itemsValue.Kind is not RedisRespReader.RespKind.Array || itemsValue.ArrayItems is null)
                throw new InvalidOperationException($"Unexpected ZSCAN items kind: {itemsValue.Kind}");

            var items = itemsValue.ArrayItems;
            var itemCount = itemsValue.ArrayLength;
            if (itemCount % 2 != 0)
                throw new InvalidOperationException("ZSCAN returned an odd number of items.");

            var result = new (byte[] Member, double Score)[itemCount / 2];
            var idx = 0;
            for (var i = 0; i < itemCount; i += 2)
            {
                if (items[i].Kind is not RedisRespReader.RespKind.BulkString)
                    throw new InvalidOperationException($"Unexpected ZSCAN member kind: {items[i].Kind}");
                var member = items[i].Bulk ?? Array.Empty<byte>();
                var score = ParseDouble(items[i + 1]);
                result[idx++] = (member, score);
            }

            return (cursorValue, result);
        }
        finally
        {
            RedisRespReader.ReturnBuffers(resp);
        }
    }

    // ========== Server Commands ==========

    public async ValueTask<string> PingAsync(CancellationToken ct)
    {
        using var activity = StartCommandActivity("PING");
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            var cmd = RedisRespProtocol.PingCommand;
            var resp = await conn.ExecuteAsync(
                cmd,
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: null).ConfigureAwait(false);

            return resp.Kind == RedisRespReader.RespKind.SimpleString
                ? resp.Text ?? string.Empty
                : throw new InvalidOperationException($"Unexpected PING response: {resp.Kind}");
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

    public async ValueTask<string[]> ModuleListAsync(CancellationToken ct)
    {
        using var activity = StartCommandActivity("MODULE");
        var sw = Stopwatch.StartNew();
        if (_instrument) RedisTelemetry.CommandCalls.Add(1);

        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            var cmd = RedisRespProtocol.ModuleListCommand;
            var resp = await conn.ExecuteAsync(
                cmd,
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                headerBuffer: null).ConfigureAwait(false);

            try
            {
                if (resp.Kind is RedisRespReader.RespKind.NullArray)
                    return Array.Empty<string>();

                if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null)
                    throw new InvalidOperationException($"Unexpected MODULE LIST response: {resp.Kind}");

                var items = resp.ArrayItems;
                var count = resp.ArrayLength;
                var result = new string[count];
                for (var i = 0; i < count; i++)
                {
                    // Each module is returned as an array with metadata
                    // We just extract the module name from the first element
                    var entry = items[i];
                    var moduleInfo = entry.ArrayItems;
                    var entryCount = entry.ArrayLength;
                    if (entry.Kind is not RedisRespReader.RespKind.Array || moduleInfo is null)
                        throw new InvalidOperationException($"Unexpected MODULE LIST item kind: {entry.Kind}");

                    if (entryCount > 1 && moduleInfo[1].Kind == RedisRespReader.RespKind.BulkString)
                    {
                        result[i] = Encoding.UTF8.GetString(moduleInfo[1].Bulk ?? Array.Empty<byte>(), 0, GetBulkLength(moduleInfo[1]));
                    }
                    else
                    {
                        result[i] = string.Empty;
                    }
                }
                return result;
            }
            finally
            {
                RedisRespReader.ReturnBuffers(resp);
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
}
