using System.Buffers;
using System.Buffers.Text;
using System.Text;

namespace VapeCache.Infrastructure.Connections;

internal static class RedisRespProtocol
{
    private static readonly byte[] OkLine = "+OK\r\n"u8.ToArray();
    private static readonly byte[] PongLine = "+PONG\r\n"u8.ToArray();

    public static ReadOnlyMemory<byte> PingCommand { get; } = "*1\r\n$4\r\nPING\r\n"u8.ToArray();

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

    public static int GetGetCommandLength(string key) => GetCommandLength(2, "GET", key);
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

    public static int GetTtlCommandLength(string key) => GetCommandLength(2, "TTL", key);
    public static int WriteTtlCommand(Span<byte> destination, string key) => WriteCommand(destination, 2, "TTL", key);

    public static int GetPTtlCommandLength(string key) => GetCommandLength(2, "PTTL", key);
    public static int WritePTtlCommand(Span<byte> destination, string key) => WriteCommand(destination, 2, "PTTL", key);

    public static int GetDelCommandLength(string key) => GetCommandLength(2, "DEL", key);
    public static int WriteDelCommand(Span<byte> destination, string key) => WriteCommand(destination, 2, "DEL", key);

    public static int GetUnlinkCommandLength(string key) => GetCommandLength(2, "UNLINK", key);
    public static int WriteUnlinkCommand(Span<byte> destination, string key) => WriteCommand(destination, 2, "UNLINK", key);

    public static int GetMGetCommandLength(string[] keys)
    {
        var count = 1 + keys.Length;
        if (count <= 1) return 0;

        var len = GetHeaderLen(count) + GetBulkLen(4) + 4 + 2; // MGET
        foreach (var k in keys)
            len += GetBulkStringLen(k) + 2;
        return len;
    }

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
        destination[0] = (byte)'$';
        var idx = 1;
        idx += WriteIntAscii(destination.Slice(idx), value.Length);
        destination[idx++] = (byte)'\r';
        destination[idx++] = (byte)'\n';

        value.CopyTo(destination.Slice(idx));
        idx += value.Length;

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

    private static int WriteIntAscii(Span<byte> destination, int value)
    {
        if (!Utf8Formatter.TryFormat(value, destination, out var written))
            throw new InvalidOperationException("Failed to format int.");
        return written;
    }
}
