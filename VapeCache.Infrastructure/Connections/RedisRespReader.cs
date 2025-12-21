using System.Buffers;
using System.Globalization;

namespace VapeCache.Infrastructure.Connections;

internal static class RedisRespReader
{
    public static async ValueTask<RespValue> ReadAsync(Stream stream, CancellationToken ct)
    {
        var prefix = await ReadByteAsync(stream, ct).ConfigureAwait(false);
        return prefix switch
        {
            (byte)'+' => RespValue.SimpleString(await RedisRespProtocol.ReadLineAsync(stream, ct).ConfigureAwait(false)),
            (byte)'-' => RespValue.Error(await RedisRespProtocol.ReadLineAsync(stream, ct).ConfigureAwait(false)),
            (byte)':' => RespValue.Integer(ReadInt64(await RedisRespProtocol.ReadLineAsync(stream, ct).ConfigureAwait(false))),
            (byte)'$' => await ReadBulkStringAsync(stream, ct).ConfigureAwait(false),
            (byte)'*' => await ReadArrayAsync(stream, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported RESP type: {(char)prefix}")
        };
    }

    private static async ValueTask<byte> ReadByteAsync(Stream stream, CancellationToken ct)
    {
        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(1);
            var read = await stream.ReadAsync(rented.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            return rented[0];
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static long ReadInt64(string line)
    {
        if (!long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException($"Invalid integer response: {line}");
        return value;
    }

    private static async ValueTask<RespValue> ReadBulkStringAsync(Stream stream, CancellationToken ct)
    {
        var header = await RedisRespProtocol.ReadLineAsync(stream, ct).ConfigureAwait(false);
        if (!int.TryParse(header, out var len))
            throw new InvalidOperationException($"Invalid bulk length: {header}");

        if (len == -1)
            return RespValue.NullBulkString();

        if (len < 0)
            throw new InvalidOperationException($"Invalid bulk length: {header}");

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

        return RespValue.BulkString(buf);
    }

    private static async ValueTask<RespValue> ReadArrayAsync(Stream stream, CancellationToken ct)
    {
        var header = await RedisRespProtocol.ReadLineAsync(stream, ct).ConfigureAwait(false);
        if (!int.TryParse(header, out var len))
            throw new InvalidOperationException($"Invalid array length: {header}");

        if (len == -1)
            return RespValue.NullArray();

        if (len < 0)
            throw new InvalidOperationException($"Invalid array length: {header}");

        var items = new RespValue[len];
        for (var i = 0; i < len; i++)
            items[i] = await ReadAsync(stream, ct).ConfigureAwait(false);

        return RespValue.Array(items);
    }

    internal readonly record struct RespValue
    {
        private RespValue(RespKind kind, string? text, byte[]? bulk, long integer, RespValue[]? array)
        {
            Kind = kind;
            Text = text;
            Bulk = bulk;
            IntegerValue = integer;
            ArrayItems = array;
        }

        public RespKind Kind { get; }
        public string? Text { get; }
        public byte[]? Bulk { get; }
        public long IntegerValue { get; }
        public RespValue[]? ArrayItems { get; }

        public static RespValue SimpleString(string s) => new(RespKind.SimpleString, s, null, 0, null);
        public static RespValue Error(string s) => new(RespKind.Error, s, null, 0, null);
        public static RespValue Integer(long v) => new(RespKind.Integer, null, null, v, null);
        public static RespValue BulkString(byte[] bytes) => new(RespKind.BulkString, null, bytes, 0, null);
        public static RespValue NullBulkString() => new(RespKind.NullBulkString, null, null, 0, null);
        public static RespValue Array(RespValue[] items) => new(RespKind.Array, null, null, 0, items);
        public static RespValue NullArray() => new(RespKind.NullArray, null, null, 0, null);
    }

    internal enum RespKind
    {
        SimpleString,
        Error,
        Integer,
        BulkString,
        NullBulkString,
        Array,
        NullArray
    }
}
