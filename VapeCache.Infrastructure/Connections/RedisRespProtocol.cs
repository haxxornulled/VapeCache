using System.Buffers;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Text;

namespace VapeCache.Infrastructure.Connections;

internal static class RedisRespProtocol
{
    private static readonly byte[] OkLine = "+OK\r\n"u8.ToArray();
    private static readonly byte[] PongLine = "+PONG\r\n"u8.ToArray();

    public static ReadOnlyMemory<byte> PingCommand { get; } = "*1\r\n$4\r\nPING\r\n"u8.ToArray();

    // QUICK WIN #2: Cached common RESP command prefixes to avoid repeated encoding
    // These are the most frequently used command prefixes in hot paths
    // Format: Array header + command bulk string header + command name + key bulk string prefix
    private static readonly byte[] GetCommandPrefix = "*2\r\n$3\r\nGET\r\n$"u8.ToArray();
    private static readonly byte[] SetCommandPrefix = "*3\r\n$3\r\nSET\r\n$"u8.ToArray();
    private static readonly byte[] SetWithTtlCommandPrefix = "*5\r\n$3\r\nSET\r\n$"u8.ToArray();
    private static readonly byte[] DelCommandPrefix = "*2\r\n$3\r\nDEL\r\n$"u8.ToArray();
    private static readonly byte[] UnlinkCommandPrefix = "*2\r\n$6\r\nUNLINK\r\n$"u8.ToArray();
    private static readonly byte[] HGetCommandPrefix = "*3\r\n$4\r\nHGET\r\n$"u8.ToArray();
    private static readonly byte[] HSetCommandPrefix = "*4\r\n$4\r\nHSET\r\n$"u8.ToArray();
    private static readonly byte[] TtlCommandPrefix = "*2\r\n$3\r\nTTL\r\n$"u8.ToArray();
    private static readonly byte[] PTtlCommandPrefix = "*2\r\n$4\r\nPTTL\r\n$"u8.ToArray();

    // Common bulk strings for command parts
    private static readonly byte[] GetBulkString = "$3\r\nGET\r\n"u8.ToArray();
    private static readonly byte[] SetBulkString = "$3\r\nSET\r\n"u8.ToArray();
    private static readonly byte[] DelBulkString = "$3\r\nDEL\r\n"u8.ToArray();
    private static readonly byte[] MGetBulkString = "$4\r\nMGET\r\n"u8.ToArray();
    private static readonly byte[] MSetBulkString = "$4\r\nMSET\r\n"u8.ToArray();
    private static readonly byte[] HGetBulkString = "$4\r\nHGET\r\n"u8.ToArray();
    private static readonly byte[] HSetBulkString = "$4\r\nHSET\r\n"u8.ToArray();
    private static readonly byte[] PxBulkString = "$2\r\nPX\r\n"u8.ToArray();
    private static readonly byte[] CrLf = "\r\n"u8.ToArray();

    public static int GetSelectCommandLength(int database)
        => GetCommandLength(2, "SELECT", GetIntLength(database));

    public static int GetAuthCommandLength(string? username, string password)
        => string.IsNullOrEmpty(username)
            ? GetCommandLength(2, "AUTH", password)
            : GetCommandLength(3, "AUTH", username, password);

    public static int GetAclWhoAmICommandLength()
        => GetCommandLength(2, "ACL", "WHOAMI");

    public static int WriteSelectCommand(Span<byte> destination, int database)
        => WriteCommand(destination, 2, "SELECT", database);

    public static int WriteAuthCommand(Span<byte> destination, string? username, string password)
        => string.IsNullOrEmpty(username)
            ? WriteCommand(destination, 2, "AUTH", password)
            : WriteCommand(destination, 3, "AUTH", username, password);

    public static int WriteAclWhoAmICommand(Span<byte> destination)
        => WriteCommand(destination, 2, "ACL", "WHOAMI");

    // HELLO command to negotiate RESP protocol version (for Redis 6+)
    public static int GetHelloCommandLength(int protocolVersion)
        => GetCommandLength(2, "HELLO", GetIntLength(protocolVersion));

    public static int WriteHelloCommand(Span<byte> destination, int protocolVersion)
        => WriteCommand(destination, 2, "HELLO", protocolVersion);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetGetCommandLength(string key) => GetCommandLength(2, "GET", key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteGetCommand(Span<byte> destination, string key) => WriteCommand(destination, 2, "GET", key);

    public static int GetGetExCommandLength(string key, int? ttlMs)
    {
        if (ttlMs is null)
            return GetCommandLength(2, "GETEX", key);

        // GETEX key PX ttl
        return GetHeaderLen(4)
               + GetBulkLen(5) + 5 + 2 // GETEX
               + GetBulkStringLen(key) + 2
               + GetBulkLen(2) + 2 + 2 // PX
               + GetBulkLen(GetIntLength(ttlMs.Value)) + GetIntLength(ttlMs.Value) + 2;
    }

    public static int WriteGetExCommand(Span<byte> destination, string key, int? ttlMs)
    {
        if (ttlMs is null)
            return WriteCommand(destination, 2, "GETEX", key);

        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 4);
        idx += WriteBulkString(destination.Slice(idx), "GETEX");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkString(destination.Slice(idx), "PX");
        idx += WriteBulkInt(destination.Slice(idx), ttlMs.Value);
        return idx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetTtlCommandLength(string key) => GetCommandLength(2, "TTL", key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteTtlCommand(Span<byte> destination, string key) => WriteCommand(destination, 2, "TTL", key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPTtlCommandLength(string key) => GetCommandLength(2, "PTTL", key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WritePTtlCommand(Span<byte> destination, string key) => WriteCommand(destination, 2, "PTTL", key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetDelCommandLength(string key) => GetCommandLength(2, "DEL", key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteDelCommand(Span<byte> destination, string key) => WriteCommand(destination, 2, "DEL", key);

    public static int GetExpireCommandLength(string key, int seconds)
    {
        // EXPIRE key seconds
        var secondsLen = GetIntLength(seconds);
        return GetHeaderLen(3)
               + GetBulkLen(6) + 6 + 2 // EXPIRE
               + GetBulkStringLen(key) + 2
               + GetBulkLen(secondsLen) + secondsLen + 2;
    }

    public static int WriteExpireCommand(Span<byte> destination, string key, int seconds)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 3);
        idx += WriteBulkString(destination.Slice(idx), "EXPIRE");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkInt(destination.Slice(idx), seconds);
        return idx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetUnlinkCommandLength(string key) => GetCommandLength(2, "UNLINK", key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteUnlinkCommand(Span<byte> destination, string key) => WriteCommand(destination, 2, "UNLINK", key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetHGetCommandLength(string key, string field) => GetCommandLength(3, "HGET", key, field);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteHGetCommand(Span<byte> destination, string key, string field) => WriteCommand(destination, 3, "HGET", key, field);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetHSetCommandLength(string key, string field, int valueLen)
    {
        // HSET key field value
        return GetHeaderLen(4)
               + GetBulkLen(4) + 4 + 2 // HSET
               + GetBulkStringLen(key) + 2
               + GetBulkStringLen(field) + 2
               + GetBulkLen(valueLen) + valueLen + 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteHSetCommand(Span<byte> destination, string key, string field, ReadOnlySpan<byte> value)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 4);
        idx += WriteBulkString(destination.Slice(idx), "HSET");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkString(destination.Slice(idx), field);
        idx += WriteBulkBytes(destination.Slice(idx), value);
        return idx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteHSetCommandHeader(Span<byte> destination, string key, string field, int valueLen)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 4);
        idx += WriteBulkString(destination.Slice(idx), "HSET");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkString(destination.Slice(idx), field);
        idx += WriteBulkLength(destination.Slice(idx), valueLen);
        return idx;
    }

    public static int GetHMGetCommandLength(string key, string[] fields)
    {
        var count = 2 + fields.Length;
        if (fields.Length == 0) return 0;

        var len = GetHeaderLen(count)
                  + GetBulkLen(5) + 5 + 2 // HMGET
                  + GetBulkStringLen(key) + 2;
        foreach (var f in fields)
            len += GetBulkStringLen(f) + 2;
        return len;
    }

    public static int WriteHMGetCommand(Span<byte> destination, string key, string[] fields)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 2 + fields.Length);
        idx += WriteBulkString(destination.Slice(idx), "HMGET");
        idx += WriteBulkString(destination.Slice(idx), key);
        foreach (var f in fields)
            idx += WriteBulkString(destination.Slice(idx), f);
        return idx;
    }

    public static int GetLPushCommandLength(string key, int valueLen)
    {
        // LPUSH key value
        return GetHeaderLen(3)
               + GetBulkLen(5) + 5 + 2 // LPUSH
               + GetBulkStringLen(key) + 2
               + GetBulkLen(valueLen) + valueLen + 2;
    }

    public static int WriteLPushCommand(Span<byte> destination, string key, ReadOnlySpan<byte> value)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 3);
        idx += WriteBulkString(destination.Slice(idx), "LPUSH");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkBytes(destination.Slice(idx), value);
        return idx;
    }

    public static int WriteLPushCommandHeader(Span<byte> destination, string key, int valueLen)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 3);
        idx += WriteBulkString(destination.Slice(idx), "LPUSH");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkLength(destination.Slice(idx), valueLen);
        return idx;
    }

    public static int GetLPopCommandLength(string key) => GetCommandLength(2, "LPOP", key);
    public static int WriteLPopCommand(Span<byte> destination, string key) => WriteCommand(destination, 2, "LPOP", key);

    public static int GetLRangeCommandLength(string key, long start, long stop)
    {
        // LRANGE key start stop
        var startLen = GetIntLength(start);
        var stopLen = GetIntLength(stop);
        return GetHeaderLen(4)
               + GetBulkLen(6) + 6 + 2 // LRANGE
               + GetBulkStringLen(key) + 2
               + GetBulkLen(startLen) + startLen + 2
               + GetBulkLen(stopLen) + stopLen + 2;
    }

    public static int WriteLRangeCommand(Span<byte> destination, string key, long start, long stop)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 4);
        idx += WriteBulkString(destination.Slice(idx), "LRANGE");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkInt64(destination.Slice(idx), start);
        idx += WriteBulkInt64(destination.Slice(idx), stop);
        return idx;
    }

    public static int GetLIndexCommandLength(string key, long index)
    {
        // LINDEX key index
        var indexLen = GetIntLength(index);
        return GetHeaderLen(3)
               + GetBulkLen(6) + 6 + 2 // LINDEX
               + GetBulkStringLen(key) + 2
               + GetBulkLen(indexLen) + indexLen + 2;
    }

    public static int WriteLIndexCommand(Span<byte> destination, string key, long index)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 3);
        idx += WriteBulkString(destination.Slice(idx), "LINDEX");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkInt64(destination.Slice(idx), index);
        return idx;
    }

    public static int GetGetRangeCommandLength(string key, long start, long end)
    {
        // GETRANGE key start end
        var startLen = GetIntLength(start);
        var endLen = GetIntLength(end);
        return GetHeaderLen(4)
               + GetBulkLen(8) + 8 + 2 // GETRANGE
               + GetBulkStringLen(key) + 2
               + GetBulkLen(startLen) + startLen + 2
               + GetBulkLen(endLen) + endLen + 2;
    }

    public static int WriteGetRangeCommand(Span<byte> destination, string key, long start, long end)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 4);
        idx += WriteBulkString(destination.Slice(idx), "GETRANGE");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkInt64(destination.Slice(idx), start);
        idx += WriteBulkInt64(destination.Slice(idx), end);
        return idx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetMGetCommandLength(string[] keys)
    {
        var count = 1 + keys.Length;
        if (count <= 1) return 0;

        var len = GetHeaderLen(count) + GetBulkLen(4) + 4 + 2; // MGET
        foreach (var k in keys)
            len += GetBulkStringLen(k) + 2;
        return len;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteMGetCommand(Span<byte> destination, string[] keys)
    {
        var count = 1 + keys.Length;
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), count);
        idx += WriteBulkString(destination.Slice(idx), "MGET");
        foreach (var k in keys)
            idx += WriteBulkString(destination.Slice(idx), k);
        return idx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetMSetCommandLength((string Key, int ValueLen)[] items)
    {
        if (items.Length == 0) return 0;
        var count = 1 + (items.Length * 2);
        var len = GetHeaderLen(count) + GetBulkLen(4) + 4 + 2; // MSET
        foreach (var (key, vlen) in items)
        {
            len += GetBulkStringLen(key) + 2;
            len += GetBulkLen(vlen) + vlen + 2;
        }
        return len;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetMSetCommandLength((string Key, ReadOnlyMemory<byte> Value)[] items)
    {
        if (items.Length == 0) return 0;
        var count = 1 + (items.Length * 2);
        var len = GetHeaderLen(count) + GetBulkLen(4) + 4 + 2; // MSET
        foreach (var (key, value) in items)
        {
            len += GetBulkStringLen(key) + 2;
            len += GetBulkLen(value.Length) + value.Length + 2;
        }
        return len;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteMSetCommand(Span<byte> destination, (string Key, ReadOnlyMemory<byte> Value)[] items)
    {
        var count = 1 + (items.Length * 2);
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), count);
        idx += WriteBulkString(destination.Slice(idx), "MSET");
        foreach (var (key, val) in items)
        {
            idx += WriteBulkString(destination.Slice(idx), key);
            idx += WriteBulkBytes(destination.Slice(idx), val.Span);
        }
        return idx;
    }

    public static int GetMSetHeaderLength((string Key, int ValueLen)[] items)
    {
        if (items.Length == 0) return 0;
        var total = GetMSetCommandLength(items);
        var payloadAndSuffix = 0;
        foreach (var (_, vlen) in items)
            payloadAndSuffix += vlen + 2; // payload + CRLF
        return total - payloadAndSuffix;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteMSetCommandHeader(Span<byte> destination, ReadOnlySpan<(string Key, int ValueLen)> items)
    {
        if (items.Length == 0) return 0;
        var count = 1 + (items.Length * 2);
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), count);
        idx += WriteBulkString(destination.Slice(idx), "MSET");
        foreach (var (key, vlen) in items)
        {
            idx += WriteBulkString(destination.Slice(idx), key);
            idx += WriteBulkLength(destination.Slice(idx), vlen);
        }
        return idx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetSetCommandLength(string key, int valueByteLen, int? ttlMs)
    {
        if (ttlMs is null)
            return GetHeaderLen(3)
                   + GetBulkLen(3) + 3 + 2 // SET
                   + GetBulkStringLen(key) + 2
                   + GetBulkLen(valueByteLen) + valueByteLen + 2;

        // SET key value PX ttl
        return GetHeaderLen(5)
               + GetBulkLen(3) + 3 + 2 // SET
               + GetBulkStringLen(key) + 2
               + GetBulkLen(valueByteLen) + valueByteLen + 2
               + GetBulkLen(2) + 2 + 2 // PX
               + GetBulkLen(GetIntLength(ttlMs.Value)) + GetIntLength(ttlMs.Value) + 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteSetCommand(Span<byte> destination, string key, ReadOnlySpan<byte> value, int? ttlMs)
    {
        if (ttlMs is null)
        {
            var idx = 0;
            idx += WriteArrayHeader(destination.Slice(idx), 3);
            idx += WriteBulkString(destination.Slice(idx), "SET");
            idx += WriteBulkString(destination.Slice(idx), key);
            idx += WriteBulkBytes(destination.Slice(idx), value);
            return idx;
        }
        else
        {
            var idx = 0;
            idx += WriteArrayHeader(destination.Slice(idx), 5);
            idx += WriteBulkString(destination.Slice(idx), "SET");
            idx += WriteBulkString(destination.Slice(idx), key);
            idx += WriteBulkBytes(destination.Slice(idx), value);
            idx += WriteBulkString(destination.Slice(idx), "PX");
            idx += WriteBulkInt(destination.Slice(idx), ttlMs.Value);
            return idx;
        }
    }

    // Header-only variant (omits value bytes and trailing CRLF) for scatter/gather writes.
    // This is only used for SET without TTL, since SET with TTL requires building the full command.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteSetCommandHeader(Span<byte> destination, string key, int valueByteLen, int? ttlMs)
    {
        // Only support SET without TTL for scatter/gather writes
        // SET with TTL must use WriteSetCommand to build the full command
        if (ttlMs is not null)
            throw new InvalidOperationException("WriteSetCommandHeader does not support TTL. Use WriteSetCommand instead.");

        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 3);
        idx += WriteBulkString(destination.Slice(idx), "SET");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkLength(destination.Slice(idx), valueByteLen);
        return idx;
    }

    public static byte[] BuildCommand(params string[] parts)
    {
        var sb = new StringBuilder();
        sb.Append('*').Append(parts.Length).Append("\r\n");
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            sb.Append('$').Append(bytes.Length).Append("\r\n");
            sb.Append(part).Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(256);
        var count = 0;
        try
        {
            while (true)
            {
                if (count == buf.Length)
                {
                    var bigger = ArrayPool<byte>.Shared.Rent(buf.Length * 2);
                    Buffer.BlockCopy(buf, 0, bigger, 0, count);
                    ArrayPool<byte>.Shared.Return(buf);
                    buf = bigger;
                }

                var read = await stream.ReadAsync(buf.AsMemory(count, 1), ct).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                count++;
                if (count >= 2 && buf[count - 2] == (byte)'\r' && buf[count - 1] == (byte)'\n')
                    return Encoding.UTF8.GetString(buf, 0, count - 2);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    public static async Task ExpectOkAsync(Stream stream, CancellationToken ct)
    {
        await ExpectExactAsync(stream, OkLine, ct).ConfigureAwait(false);
    }

    public static async Task<string> ReadBulkStringAsync(Stream stream, CancellationToken ct)
    {
        var header = await ReadLineAsync(stream, ct).ConfigureAwait(false);
        if (header.StartsWith("-", StringComparison.Ordinal)) throw new InvalidOperationException($"Redis error: {header}");
        if (!header.StartsWith("$", StringComparison.Ordinal)) throw new InvalidOperationException($"Unexpected Redis response: {header}");

        if (!int.TryParse(header.AsSpan(1), out var len) || len < 0)
            throw new InvalidOperationException($"Unexpected Redis bulk string length: {header}");

        var buf = new byte[len];
        var read = 0;
        while (read < len)
        {
            var n = await stream.ReadAsync(buf.AsMemory(read, len - read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }

        // consume CRLF
        var crlf = new byte[2];
        var crlfRead = 0;
        while (crlfRead < 2)
        {
            var n = await stream.ReadAsync(crlf.AsMemory(crlfRead, 2 - crlfRead), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            crlfRead += n;
        }

        return Encoding.UTF8.GetString(buf);
    }

    public static async Task ExpectPongAsync(Stream stream, CancellationToken ct)
    {
        await ExpectExactAsync(stream, PongLine, ct).ConfigureAwait(false);
    }

    // HELLO response is a map/array in RESP2 - just skip it recursively
    public static async Task SkipHelloResponseAsync(Stream stream, CancellationToken ct)
    {
        await SkipRespValueAsync(stream, ct).ConfigureAwait(false);
    }

    // Recursively skip any RESP value (array, bulk string, simple string, integer, error)
    private static async Task SkipRespValueAsync(Stream stream, CancellationToken ct)
    {
        var line = await ReadLineAsync(stream, ct).ConfigureAwait(false);

        if (line.StartsWith("-", StringComparison.Ordinal))
            throw new InvalidOperationException($"Redis error: {line}");

        if (line.StartsWith("*", StringComparison.Ordinal)) // Array
        {
            if (!int.TryParse(line.AsSpan(1), out var count))
                throw new InvalidOperationException($"Invalid array response: {line}");

            // Recursively skip all array elements
            for (int i = 0; i < count; i++)
            {
                await SkipRespValueAsync(stream, ct).ConfigureAwait(false);
            }
        }
        else if (line.StartsWith("$", StringComparison.Ordinal)) // Bulk string
        {
            if (!int.TryParse(line.AsSpan(1), out var len))
                throw new InvalidOperationException($"Invalid bulk string length: {line}");

            if (len >= 0)
            {
                // Read the bulk string data + CRLF
                var skip = new byte[len + 2];
                await ReadExactAsync(stream, skip, ct).ConfigureAwait(false);
            }
        }
        // Simple string (+), integer (:), and null ($-1) are already consumed by ReadLineAsync
    }

    public static async Task<bool> TryExpectOkAsync(Stream stream, CancellationToken ct)
    {
        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(OkLine.Length);
            var buf = rented.AsMemory(0, OkLine.Length);
            await ReadExactAsync(stream, buf, ct).ConfigureAwait(false);

            var span = rented.AsSpan(0, OkLine.Length);
            if (span.SequenceEqual(OkLine)) return true;

            // If it's an error line, drain the rest of the line so the connection stays in sync.
            if (span.Length > 0 && span[0] == (byte)'-')
            {
                await DrainLineAsync(stream, ct).ConfigureAwait(false);
                return false;
            }

            throw new InvalidOperationException("Unexpected Redis response.");
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static async Task ExpectExactAsync(Stream stream, byte[] expected, CancellationToken ct)
    {
        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(expected.Length);
            var buf = rented.AsMemory(0, expected.Length);
            await ReadExactAsync(stream, buf, ct).ConfigureAwait(false);

            var span = rented.AsSpan(0, expected.Length);
            if (span.SequenceEqual(expected)) return;

            // If response begins with '-', drain to end-of-line for better errors (connection will be dropped anyway).
            if (span.Length > 0 && span[0] == (byte)'-')
            {
                await DrainLineAsync(stream, ct).ConfigureAwait(false);
                throw new InvalidOperationException("Redis error response.");
            }

            throw new InvalidOperationException("Unexpected Redis response.");
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.Slice(read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }

    private static async Task DrainLineAsync(Stream stream, CancellationToken ct)
    {
        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(256);
            while (true)
            {
                var n = await stream.ReadAsync(rented.AsMemory(0, rented.Length), ct).ConfigureAwait(false);
                if (n == 0) return;
                for (var i = 0; i < n; i++)
                {
                    if (rented[i] == (byte)'\n')
                        return;
                }
            }
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static int GetCommandLength(int partsCount, string p0, string p1)
    {
        var b0 = Encoding.UTF8.GetByteCount(p0);
        var b1 = Encoding.UTF8.GetByteCount(p1);
        return GetHeaderLen(partsCount)
               + GetBulkLen(b0) + b0 + 2
               + GetBulkLen(b1) + b1 + 2;
    }

    private static int GetCommandLength(int partsCount, string p0, string p1, string p2)
    {
        var b0 = Encoding.UTF8.GetByteCount(p0);
        var b1 = Encoding.UTF8.GetByteCount(p1);
        var b2 = Encoding.UTF8.GetByteCount(p2);
        return GetHeaderLen(partsCount)
               + GetBulkLen(b0) + b0 + 2
               + GetBulkLen(b1) + b1 + 2
               + GetBulkLen(b2) + b2 + 2;
    }

    private static int GetCommandLength(int partsCount, string p0, int p1IntLen)
    {
        var b0 = Encoding.UTF8.GetByteCount(p0);
        return GetHeaderLen(partsCount)
               + GetBulkLen(b0) + b0 + 2
               + GetBulkLen(p1IntLen) + p1IntLen + 2;
    }

    private static int GetHeaderLen(int partsCount) => 1 + GetIntLength(partsCount) + 2;

    private static int GetBulkLen(int byteLen) => 1 + GetIntLength(byteLen) + 2;

    private static int GetBulkStringLen(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        return GetBulkLen(byteCount) + byteCount;
    }

    private static int GetIntLength(int value)
        => value switch
        {
            < 0 => 1 + GetIntLength(-value),
            < 10 => 1,
            < 100 => 2,
            < 1000 => 3,
            < 10000 => 4,
            < 100000 => 5,
            < 1000000 => 6,
            < 10000000 => 7,
            < 100000000 => 8,
            < 1000000000 => 9,
            _ => 10
        };

    private static int GetIntLength(long value)
    {
        if (value == long.MinValue) return 20;
        if (value < 0) return 1 + GetIntLength(-value);
        if (value < 10L) return 1;
        if (value < 100L) return 2;
        if (value < 1000L) return 3;
        if (value < 10000L) return 4;
        if (value < 100000L) return 5;
        if (value < 1000000L) return 6;
        if (value < 10000000L) return 7;
        if (value < 100000000L) return 8;
        if (value < 1000000000L) return 9;
        if (value < 10000000000L) return 10;
        if (value < 100000000000L) return 11;
        if (value < 1000000000000L) return 12;
        if (value < 10000000000000L) return 13;
        if (value < 100000000000000L) return 14;
        if (value < 1000000000000000L) return 15;
        if (value < 10000000000000000L) return 16;
        if (value < 100000000000000000L) return 17;
        if (value < 1000000000000000000L) return 18;
        return 19;
    }

    private static int WriteCommand(Span<byte> destination, int partsCount, string p0, string p1)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), partsCount);
        idx += WriteBulkString(destination.Slice(idx), p0);
        idx += WriteBulkString(destination.Slice(idx), p1);
        return idx;
    }

    private static int WriteCommand(Span<byte> destination, int partsCount, string p0, string p1, string p2)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), partsCount);
        idx += WriteBulkString(destination.Slice(idx), p0);
        idx += WriteBulkString(destination.Slice(idx), p1);
        idx += WriteBulkString(destination.Slice(idx), p2);
        return idx;
    }

    private static int WriteCommand(Span<byte> destination, int partsCount, string p0, int p1)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), partsCount);
        idx += WriteBulkString(destination.Slice(idx), p0);
        idx += WriteBulkInt(destination.Slice(idx), p1);
        return idx;
    }

    private static int WriteCommand(Span<byte> destination, int partsCount, string p0, string p1, int p2)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), partsCount);
        idx += WriteBulkString(destination.Slice(idx), p0);
        idx += WriteBulkString(destination.Slice(idx), p1);
        idx += WriteBulkInt(destination.Slice(idx), p2);
        return idx;
    }

    private static int WriteArrayHeader(Span<byte> destination, int partsCount)
    {
        destination[0] = (byte)'*';
        var idx = 1;
        idx += WriteIntAscii(destination.Slice(idx), partsCount);
        destination[idx++] = (byte)'\r';
        destination[idx++] = (byte)'\n';
        return idx;
    }

    private static int WriteBulkString(Span<byte> destination, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        destination[0] = (byte)'$';
        var idx = 1;
        idx += WriteIntAscii(destination.Slice(idx), byteCount);
        destination[idx++] = (byte)'\r';
        destination[idx++] = (byte)'\n';

        Encoding.UTF8.GetBytes(value, destination.Slice(idx, byteCount));
        idx += byteCount;

        destination[idx++] = (byte)'\r';
        destination[idx++] = (byte)'\n';
        return idx;
    }

    private static int WriteBulkBytes(Span<byte> destination, ReadOnlySpan<byte> value)
    {
        var idx = WriteBulkLength(destination, value.Length);
        value.CopyTo(destination.Slice(idx));
        idx += value.Length;

        destination[idx++] = (byte)'\r';
        destination[idx++] = (byte)'\n';
        return idx;
    }

    internal static int WriteBulkLength(Span<byte> destination, int length)
    {
        destination[0] = (byte)'$';
        var idx = 1;
        idx += WriteIntAscii(destination.Slice(idx), length);
        destination[idx++] = (byte)'\r';
        destination[idx++] = (byte)'\n';
        return idx;
    }

    private static int WriteBulkInt(Span<byte> destination, int value)
    {
        destination[0] = (byte)'$';
        var idx = 1;

        Span<byte> tmp = stackalloc byte[32];
        if (!Utf8Formatter.TryFormat(value, tmp, out var written))
            throw new InvalidOperationException("Failed to format int.");

        idx += WriteIntAscii(destination.Slice(idx), written);
        destination[idx++] = (byte)'\r';
        destination[idx++] = (byte)'\n';

        tmp.Slice(0, written).CopyTo(destination.Slice(idx));
        idx += written;

        destination[idx++] = (byte)'\r';
        destination[idx++] = (byte)'\n';
        return idx;
    }

    private static int WriteBulkLong(Span<byte> destination, long value)
    {
        destination[0] = (byte)'$';
        var idx = 1;

        Span<byte> tmp = stackalloc byte[32];
        if (!Utf8Formatter.TryFormat(value, tmp, out var written))
            throw new InvalidOperationException("Failed to format long.");

        idx += WriteIntAscii(destination.Slice(idx), written);
        destination[idx++] = (byte)'\r';
        destination[idx++] = (byte)'\n';

        tmp.Slice(0, written).CopyTo(destination.Slice(idx));
        idx += written;

        destination[idx++] = (byte)'\r';
        destination[idx++] = (byte)'\n';
        return idx;
    }

    private static int WriteBulkInt64(Span<byte> destination, long value)
    {
        destination[0] = (byte)'$';
        var idx = 1;

        Span<byte> tmp = stackalloc byte[32];
        if (!Utf8Formatter.TryFormat(value, tmp, out var written))
            throw new InvalidOperationException("Failed to format long.");

        idx += WriteIntAscii(destination.Slice(idx), written);
        destination[idx++] = (byte)'\r';
        destination[idx++] = (byte)'\n';

        tmp.Slice(0, written).CopyTo(destination.Slice(idx));
        idx += written;

        destination[idx++] = (byte)'\r';
        destination[idx++] = (byte)'\n';
        return idx;
    }

    private static int WriteIntAscii(Span<byte> destination, int value)
    {
        if (!Utf8Formatter.TryFormat(value, destination, out var written))
            throw new InvalidOperationException("Failed to format int.");
        return written;
    }

    // ======================
    // NEW COMMANDS - Phase 1
    // ======================

    // RPUSH (push to tail of list)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetRPushCommandLength(string key, int valueLen)
    {
        // RPUSH key value
        return GetHeaderLen(3)
               + GetBulkLen(5) + 5 + 2 // RPUSH
               + GetBulkStringLen(key) + 2
               + GetBulkLen(valueLen) + valueLen + 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteRPushCommand(Span<byte> destination, string key, ReadOnlySpan<byte> value)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 3);
        idx += WriteBulkString(destination.Slice(idx), "RPUSH");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkBytes(destination.Slice(idx), value);
        return idx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetRPushManyCommandLength(string key, ReadOnlyMemory<byte>[] values, int count)
    {
        if (count < 0 || count > values.Length)
            throw new ArgumentOutOfRangeException(nameof(count));

        // RPUSH key v1 v2 ... vN
        var len = GetHeaderLen(2 + count)
                  + GetBulkLen(5) + 5 + 2 // RPUSH
                  + GetBulkStringLen(key) + 2;

        for (var i = 0; i < count; i++)
        {
            var valueLen = values[i].Length;
            len += GetBulkLen(valueLen) + valueLen + 2;
        }

        return len;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteRPushManyCommand(Span<byte> destination, string key, ReadOnlyMemory<byte>[] values, int count)
    {
        if (count < 0 || count > values.Length)
            throw new ArgumentOutOfRangeException(nameof(count));

        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 2 + count);
        idx += WriteBulkString(destination.Slice(idx), "RPUSH");
        idx += WriteBulkString(destination.Slice(idx), key);

        for (var i = 0; i < count; i++)
            idx += WriteBulkBytes(destination.Slice(idx), values[i].Span);

        return idx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetRPushManyPrefixLength(string key, int count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        return GetHeaderLen(2 + count)
               + GetBulkLen(5) + 5 + 2 // RPUSH
               + GetBulkStringLen(key) + 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteRPushManyPrefix(Span<byte> destination, string key, int count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 2 + count);
        idx += WriteBulkString(destination.Slice(idx), "RPUSH");
        idx += WriteBulkString(destination.Slice(idx), key);
        return idx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetBulkLengthPrefixLength(int payloadLength)
        => GetBulkLen(payloadLength);

    // RPOP (pop from tail of list)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetRPopCommandLength(string key) => GetCommandLength(2, "RPOP", key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteRPopCommand(Span<byte> destination, string key) => WriteCommand(destination, 2, "RPOP", key);

    // LLEN (get list length)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetLLenCommandLength(string key) => GetCommandLength(2, "LLEN", key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteLLenCommand(Span<byte> destination, string key) => WriteCommand(destination, 2, "LLEN", key);

    // SADD (add to set)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetSAddCommandLength(string key, int memberLen)
    {
        // SADD key member
        return GetHeaderLen(3)
               + GetBulkLen(4) + 4 + 2 // SADD
               + GetBulkStringLen(key) + 2
               + GetBulkLen(memberLen) + memberLen + 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteSAddCommand(Span<byte> destination, string key, ReadOnlySpan<byte> member)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 3);
        idx += WriteBulkString(destination.Slice(idx), "SADD");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkBytes(destination.Slice(idx), member);
        return idx;
    }

    // SREM (remove from set)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetSRemCommandLength(string key, int memberLen)
    {
        // SREM key member
        return GetHeaderLen(3)
               + GetBulkLen(4) + 4 + 2 // SREM
               + GetBulkStringLen(key) + 2
               + GetBulkLen(memberLen) + memberLen + 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteSRemCommand(Span<byte> destination, string key, ReadOnlySpan<byte> member)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 3);
        idx += WriteBulkString(destination.Slice(idx), "SREM");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkBytes(destination.Slice(idx), member);
        return idx;
    }

    // SISMEMBER (check set membership)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetSIsMemberCommandLength(string key, int memberLen)
    {
        // SISMEMBER key member
        return GetHeaderLen(3)
               + GetBulkLen(9) + 9 + 2 // SISMEMBER
               + GetBulkStringLen(key) + 2
               + GetBulkLen(memberLen) + memberLen + 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteSIsMemberCommand(Span<byte> destination, string key, ReadOnlySpan<byte> member)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 3);
        idx += WriteBulkString(destination.Slice(idx), "SISMEMBER");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkBytes(destination.Slice(idx), member);
        return idx;
    }

    // SMEMBERS (get all set members)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetSMembersCommandLength(string key) => GetCommandLength(2, "SMEMBERS", key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteSMembersCommand(Span<byte> destination, string key) => WriteCommand(destination, 2, "SMEMBERS", key);

    // SCARD (get set cardinality/count)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetSCardCommandLength(string key) => GetCommandLength(2, "SCARD", key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteSCardCommand(Span<byte> destination, string key) => WriteCommand(destination, 2, "SCARD", key);

    // ZADD (add/update sorted set member)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetZAddCommandLength(string key, string score, int memberLen)
    {
        return GetHeaderLen(4)
               + GetBulkStringLen("ZADD") + 2
               + GetBulkStringLen(key) + 2
               + GetBulkStringLen(score) + 2
               + GetBulkLen(memberLen) + memberLen + 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteZAddCommand(Span<byte> destination, string key, string score, ReadOnlySpan<byte> member)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 4);
        idx += WriteBulkString(destination.Slice(idx), "ZADD");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkString(destination.Slice(idx), score);
        idx += WriteBulkBytes(destination.Slice(idx), member);
        return idx;
    }

    // ZREM (remove sorted set member)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetZRemCommandLength(string key, int memberLen)
    {
        return GetHeaderLen(3)
               + GetBulkStringLen("ZREM") + 2
               + GetBulkStringLen(key) + 2
               + GetBulkLen(memberLen) + memberLen + 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteZRemCommand(Span<byte> destination, string key, ReadOnlySpan<byte> member)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 3);
        idx += WriteBulkString(destination.Slice(idx), "ZREM");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkBytes(destination.Slice(idx), member);
        return idx;
    }

    // ZCARD (sorted set cardinality)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetZCardCommandLength(string key) => GetCommandLength(2, "ZCARD", key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteZCardCommand(Span<byte> destination, string key) => WriteCommand(destination, 2, "ZCARD", key);

    // ZSCORE (member score)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetZScoreCommandLength(string key, int memberLen)
    {
        return GetHeaderLen(3)
               + GetBulkStringLen("ZSCORE") + 2
               + GetBulkStringLen(key) + 2
               + GetBulkLen(memberLen) + memberLen + 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteZScoreCommand(Span<byte> destination, string key, ReadOnlySpan<byte> member)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 3);
        idx += WriteBulkString(destination.Slice(idx), "ZSCORE");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkBytes(destination.Slice(idx), member);
        return idx;
    }

    // ZRANK/ZREVRANK (member rank)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetZRankCommandLength(string key, int memberLen, bool descending)
    {
        var command = descending ? "ZREVRANK" : "ZRANK";
        return GetHeaderLen(3)
               + GetBulkStringLen(command) + 2
               + GetBulkStringLen(key) + 2
               + GetBulkLen(memberLen) + memberLen + 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteZRankCommand(Span<byte> destination, string key, ReadOnlySpan<byte> member, bool descending)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 3);
        idx += WriteBulkString(destination.Slice(idx), descending ? "ZREVRANK" : "ZRANK");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkBytes(destination.Slice(idx), member);
        return idx;
    }

    // ZINCRBY (increment score)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetZIncrByCommandLength(string key, string increment, int memberLen)
    {
        return GetHeaderLen(4)
               + GetBulkStringLen("ZINCRBY") + 2
               + GetBulkStringLen(key) + 2
               + GetBulkStringLen(increment) + 2
               + GetBulkLen(memberLen) + memberLen + 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteZIncrByCommand(Span<byte> destination, string key, string increment, ReadOnlySpan<byte> member)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 4);
        idx += WriteBulkString(destination.Slice(idx), "ZINCRBY");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkString(destination.Slice(idx), increment);
        idx += WriteBulkBytes(destination.Slice(idx), member);
        return idx;
    }

    // ZRANGE/ZREVRANGE (with scores)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetZRangeWithScoresCommandLength(string key, long start, long stop, bool descending)
    {
        var startLen = GetIntLength(start);
        var stopLen = GetIntLength(stop);
        var command = descending ? "ZREVRANGE" : "ZRANGE";

        return GetHeaderLen(5)
               + GetBulkStringLen(command) + 2
               + GetBulkStringLen(key) + 2
               + GetBulkLen(startLen) + startLen + 2
               + GetBulkLen(stopLen) + stopLen + 2
               + GetBulkStringLen("WITHSCORES") + 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteZRangeWithScoresCommand(Span<byte> destination, string key, long start, long stop, bool descending)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 5);
        idx += WriteBulkString(destination.Slice(idx), descending ? "ZREVRANGE" : "ZRANGE");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkLong(destination.Slice(idx), start);
        idx += WriteBulkLong(destination.Slice(idx), stop);
        idx += WriteBulkString(destination.Slice(idx), "WITHSCORES");
        return idx;
    }

    // ZRANGEBYSCORE/ZREVRANGEBYSCORE (with scores)
    public static int GetZRangeByScoreWithScoresCommandLength(
        string key,
        string min,
        string max,
        bool descending,
        long? offset,
        long? count)
    {
        var command = descending ? "ZREVRANGEBYSCORE" : "ZRANGEBYSCORE";
        var parts = 5;
        if (offset.HasValue && count.HasValue)
            parts += 2;

        var len = GetHeaderLen(parts)
                  + GetBulkStringLen(command) + 2
                  + GetBulkStringLen(key) + 2
                  + GetBulkStringLen(min) + 2
                  + GetBulkStringLen(max) + 2
                  + GetBulkStringLen("WITHSCORES") + 2;

        if (offset.HasValue && count.HasValue)
        {
            var offsetLen = GetIntLength(offset.Value);
            var countLen = GetIntLength(count.Value);
            len += GetBulkStringLen("LIMIT") + 2
                   + GetBulkLen(offsetLen) + offsetLen + 2
                   + GetBulkLen(countLen) + countLen + 2;
        }

        return len;
    }

    public static int WriteZRangeByScoreWithScoresCommand(
        Span<byte> destination,
        string key,
        string min,
        string max,
        bool descending,
        long? offset,
        long? count)
    {
        var parts = 5;
        if (offset.HasValue && count.HasValue)
            parts += 2;

        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), parts);
        idx += WriteBulkString(destination.Slice(idx), descending ? "ZREVRANGEBYSCORE" : "ZRANGEBYSCORE");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkString(destination.Slice(idx), min);
        idx += WriteBulkString(destination.Slice(idx), max);
        idx += WriteBulkString(destination.Slice(idx), "WITHSCORES");

        if (offset.HasValue && count.HasValue)
        {
            idx += WriteBulkString(destination.Slice(idx), "LIMIT");
            idx += WriteBulkLong(destination.Slice(idx), offset.Value);
            idx += WriteBulkLong(destination.Slice(idx), count.Value);
        }

        return idx;
    }

    // JSON.GET
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetJsonGetCommandLength(string key, string? path)
        => path is null
            ? GetCommandLength(2, "JSON.GET", key)
            : GetCommandLength(3, "JSON.GET", key, path);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteJsonGetCommand(Span<byte> destination, string key, string? path)
        => path is null
            ? WriteCommand(destination, 2, "JSON.GET", key)
            : WriteCommand(destination, 3, "JSON.GET", key, path);

    // JSON.SET
    public static int GetJsonSetCommandLength(string key, string path, int jsonLength)
    {
        return GetHeaderLen(4)
               + GetBulkStringLen("JSON.SET") + 2
               + GetBulkStringLen(key) + 2
               + GetBulkStringLen(path) + 2
               + GetBulkLen(jsonLength) + jsonLength + 2;
    }

    // Header-only variant (omits JSON bytes and trailing CRLF) for scatter/gather writes.
    public static int WriteJsonSetCommandHeader(Span<byte> destination, string key, string path, int jsonLength)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 4);
        idx += WriteBulkString(destination.Slice(idx), "JSON.SET");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkString(destination.Slice(idx), path);
        idx += WriteBulkLength(destination.Slice(idx), jsonLength);
        return idx;
    }

    // JSON.DEL
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetJsonDelCommandLength(string key, string? path)
        => path is null
            ? GetCommandLength(2, "JSON.DEL", key)
            : GetCommandLength(3, "JSON.DEL", key, path);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteJsonDelCommand(Span<byte> destination, string key, string? path)
        => path is null
            ? WriteCommand(destination, 2, "JSON.DEL", key)
            : WriteCommand(destination, 3, "JSON.DEL", key, path);

    // SCAN/SSCAN/HSCAN/ZSCAN
    public static int GetScanCommandLength(string command, string? key, long cursor, string? pattern, int count)
    {
        var parts = key is null ? 2 : 3;
        if (!string.IsNullOrWhiteSpace(pattern))
            parts += 2;
        if (count > 0)
            parts += 2;

        var cursorLen = GetIntLength(cursor);
        var len = GetHeaderLen(parts)
                  + GetBulkStringLen(command) + 2;

        if (key is not null)
            len += GetBulkStringLen(key) + 2;

        len += GetBulkLen(cursorLen) + cursorLen + 2;

        if (!string.IsNullOrWhiteSpace(pattern))
            len += GetBulkStringLen("MATCH") + 2 + GetBulkStringLen(pattern) + 2;

        if (count > 0)
        {
            var countLen = GetIntLength(count);
            len += GetBulkStringLen("COUNT") + 2 + GetBulkLen(countLen) + countLen + 2;
        }

        return len;
    }

    public static int WriteScanCommand(Span<byte> destination, string command, string? key, long cursor, string? pattern, int count)
    {
        var parts = key is null ? 2 : 3;
        if (!string.IsNullOrWhiteSpace(pattern))
            parts += 2;
        if (count > 0)
            parts += 2;

        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), parts);
        idx += WriteBulkString(destination.Slice(idx), command);
        if (key is not null)
            idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkLong(destination.Slice(idx), cursor);

        if (!string.IsNullOrWhiteSpace(pattern))
        {
            idx += WriteBulkString(destination.Slice(idx), "MATCH");
            idx += WriteBulkString(destination.Slice(idx), pattern);
        }

        if (count > 0)
        {
            idx += WriteBulkString(destination.Slice(idx), "COUNT");
            idx += WriteBulkInt(destination.Slice(idx), count);
        }

        return idx;
    }

    // BF.ADD / BF.EXISTS (RedisBloom)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetBfAddCommandLength(string key, int itemLen)
    {
        return GetHeaderLen(3)
               + GetBulkStringLen("BF.ADD") + 2
               + GetBulkStringLen(key) + 2
               + GetBulkLen(itemLen) + itemLen + 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteBfAddCommand(Span<byte> destination, string key, ReadOnlySpan<byte> item)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 3);
        idx += WriteBulkString(destination.Slice(idx), "BF.ADD");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkBytes(destination.Slice(idx), item);
        return idx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetBfExistsCommandLength(string key, int itemLen)
    {
        return GetHeaderLen(3)
               + GetBulkStringLen("BF.EXISTS") + 2
               + GetBulkStringLen(key) + 2
               + GetBulkLen(itemLen) + itemLen + 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteBfExistsCommand(Span<byte> destination, string key, ReadOnlySpan<byte> item)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 3);
        idx += WriteBulkString(destination.Slice(idx), "BF.EXISTS");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkBytes(destination.Slice(idx), item);
        return idx;
    }

    // FT.CREATE / FT.SEARCH (RediSearch)
    public static int GetFtCreateCommandLength(string index, string prefix, string[] fields)
    {
        var parts = 8 + fields.Length * 2;
        var len = GetHeaderLen(parts)
                  + GetBulkStringLen("FT.CREATE") + 2
                  + GetBulkStringLen(index) + 2
                  + GetBulkStringLen("ON") + 2
                  + GetBulkStringLen("HASH") + 2
                  + GetBulkStringLen("PREFIX") + 2
                  + GetBulkStringLen("1") + 2
                  + GetBulkStringLen(prefix) + 2
                  + GetBulkStringLen("SCHEMA") + 2;

        for (var i = 0; i < fields.Length; i++)
        {
            len += GetBulkStringLen(fields[i]) + 2
                   + GetBulkStringLen("TEXT") + 2;
        }

        return len;
    }

    public static int WriteFtCreateCommand(Span<byte> destination, string index, string prefix, string[] fields)
    {
        var parts = 8 + fields.Length * 2;
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), parts);
        idx += WriteBulkString(destination.Slice(idx), "FT.CREATE");
        idx += WriteBulkString(destination.Slice(idx), index);
        idx += WriteBulkString(destination.Slice(idx), "ON");
        idx += WriteBulkString(destination.Slice(idx), "HASH");
        idx += WriteBulkString(destination.Slice(idx), "PREFIX");
        idx += WriteBulkString(destination.Slice(idx), "1");
        idx += WriteBulkString(destination.Slice(idx), prefix);
        idx += WriteBulkString(destination.Slice(idx), "SCHEMA");
        for (var i = 0; i < fields.Length; i++)
        {
            idx += WriteBulkString(destination.Slice(idx), fields[i]);
            idx += WriteBulkString(destination.Slice(idx), "TEXT");
        }

        return idx;
    }

    public static int GetFtSearchCommandLength(string index, string query, int? offset, int? count)
    {
        var parts = 3;
        if (offset.HasValue && count.HasValue)
            parts += 3;

        var len = GetHeaderLen(parts)
                  + GetBulkStringLen("FT.SEARCH") + 2
                  + GetBulkStringLen(index) + 2
                  + GetBulkStringLen(query) + 2;

        if (offset.HasValue && count.HasValue)
        {
            var offsetLen = GetIntLength(offset.Value);
            var countLen = GetIntLength(count.Value);
            len += GetBulkStringLen("LIMIT") + 2
                   + GetBulkLen(offsetLen) + offsetLen + 2
                   + GetBulkLen(countLen) + countLen + 2;
        }

        return len;
    }

    public static int WriteFtSearchCommand(Span<byte> destination, string index, string query, int? offset, int? count)
    {
        var parts = 3;
        if (offset.HasValue && count.HasValue)
            parts += 3;

        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), parts);
        idx += WriteBulkString(destination.Slice(idx), "FT.SEARCH");
        idx += WriteBulkString(destination.Slice(idx), index);
        idx += WriteBulkString(destination.Slice(idx), query);

        if (offset.HasValue && count.HasValue)
        {
            idx += WriteBulkString(destination.Slice(idx), "LIMIT");
            idx += WriteBulkLong(destination.Slice(idx), offset.Value);
            idx += WriteBulkLong(destination.Slice(idx), count.Value);
        }

        return idx;
    }

    // TS.CREATE / TS.ADD / TS.RANGE (RedisTimeSeries)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetTsCreateCommandLength(string key) => GetCommandLength(2, "TS.CREATE", key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteTsCreateCommand(Span<byte> destination, string key) => WriteCommand(destination, 2, "TS.CREATE", key);

    public static int GetTsAddCommandLength(string key, long timestamp, string value)
    {
        var tsLen = GetIntLength(timestamp);
        return GetHeaderLen(4)
               + GetBulkStringLen("TS.ADD") + 2
               + GetBulkStringLen(key) + 2
               + GetBulkLen(tsLen) + tsLen + 2
               + GetBulkStringLen(value) + 2;
    }

    public static int WriteTsAddCommand(Span<byte> destination, string key, long timestamp, string value)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 4);
        idx += WriteBulkString(destination.Slice(idx), "TS.ADD");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkLong(destination.Slice(idx), timestamp);
        idx += WriteBulkString(destination.Slice(idx), value);
        return idx;
    }

    public static int GetTsRangeCommandLength(string key, long from, long to)
    {
        var fromLen = GetIntLength(from);
        var toLen = GetIntLength(to);
        return GetHeaderLen(4)
               + GetBulkStringLen("TS.RANGE") + 2
               + GetBulkStringLen(key) + 2
               + GetBulkLen(fromLen) + fromLen + 2
               + GetBulkLen(toLen) + toLen + 2;
    }

    public static int WriteTsRangeCommand(Span<byte> destination, string key, long from, long to)
    {
        var idx = 0;
        idx += WriteArrayHeader(destination.Slice(idx), 4);
        idx += WriteBulkString(destination.Slice(idx), "TS.RANGE");
        idx += WriteBulkString(destination.Slice(idx), key);
        idx += WriteBulkLong(destination.Slice(idx), from);
        idx += WriteBulkLong(destination.Slice(idx), to);
        return idx;
    }

    // MODULE LIST (detect installed modules like RedisJSON)
    public static ReadOnlyMemory<byte> ModuleListCommand { get; } = "*2\r\n$6\r\nMODULE\r\n$4\r\nLIST\r\n"u8.ToArray();
}
