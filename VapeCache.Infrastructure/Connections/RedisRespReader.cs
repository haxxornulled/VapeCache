using System.Buffers;
using System.Globalization;

namespace VapeCache.Infrastructure.Connections;

internal static class RedisRespReader
{
    internal static readonly string OkSimpleString = "OK";
    private const int MaxCachedArrayLength = 16;
    private static readonly object ArrayCacheLock = new();
    private static readonly RespValue[][] ArrayCache = new RespValue[32][];
    private static int _arrayCacheCount;

    internal static void ReturnBuffers(in RespValue value)
    {
        if (value.Kind == RespKind.BulkString && value.BulkIsPooled && value.Bulk is not null)
        {
            ArrayPool<byte>.Shared.Return(value.Bulk);
            return;
        }

        if (value.Kind == RespKind.Array &&
            value.ArrayIsPooled &&
            value.ArrayItems is not null)
        {
            var len = value.ArrayLength;
            for (var i = 0; i < len; i++)
                ReturnBuffers(value.ArrayItems[i]);
            ReturnArray(value.ArrayItems, len);
        }
    }

    internal static RespValue[] RentArray(int length)
    {
        if (length <= MaxCachedArrayLength)
        {
            lock (ArrayCacheLock)
            {
                if (_arrayCacheCount > 0)
                {
                    var arr = ArrayCache[--_arrayCacheCount];
                    ArrayCache[_arrayCacheCount] = null!;
                    if (arr.Length >= length)
                        return arr;

                    ArrayPool<RespValue>.Shared.Return(arr, clearArray: true);
                }
            }
        }

        return ArrayPool<RespValue>.Shared.Rent(length);
    }

    internal static void ReturnArray(RespValue[] array, int length)
    {
        // Clear references to avoid holding onto nested pooled buffers.
        Array.Clear(array, 0, Math.Min(length, array.Length));

        if (length <= MaxCachedArrayLength)
        {
            lock (ArrayCacheLock)
            {
                if (_arrayCacheCount < ArrayCache.Length)
                {
                    ArrayCache[_arrayCacheCount++] = array;
                    return;
                }
            }
        }

        ArrayPool<RespValue>.Shared.Return(array, clearArray: true);
    }

    public static async ValueTask<RespValue> ReadAsync(Stream stream, CancellationToken ct)
    {
        var prefix = await ReadByteAsync(stream, ct).ConfigureAwait(false);
        return prefix switch
        {
            (byte)'+' =>
                await RedisRespProtocol.ReadLineAsync(stream, ct).ConfigureAwait(false) is { } s
                    ? (ReferenceEquals(s, OkSimpleString) || s == OkSimpleString
                        ? RespValue.SimpleString(OkSimpleString)
                        : RespValue.SimpleString(s))
                    : throw new InvalidOperationException("Invalid simple string"),
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

        var buf = GC.AllocateUninitializedArray<byte>(len);
        var read = 0;
        while (read < len)
        {
            var n = await stream.ReadAsync(buf.AsMemory(read, len - read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }

        // consume CRLF
        byte[]? crlf = null;
        try
        {
            crlf = ArrayPool<byte>.Shared.Rent(2);
            var crlfRead = 0;
            while (crlfRead < 2)
            {
                var n = await stream.ReadAsync(crlf.AsMemory(crlfRead, 2 - crlfRead), ct).ConfigureAwait(false);
                if (n == 0) throw new EndOfStreamException();
                crlfRead += n;
            }
        }
        finally
        {
            if (crlf is not null) ArrayPool<byte>.Shared.Return(crlf);
        }

        return RespValue.BulkString(buf, len, pooled: false);
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

        var items = RentArray(len);
        for (var i = 0; i < len; i++)
            items[i] = await ReadAsync(stream, ct).ConfigureAwait(false);

        return RespValue.Array(items, len, pooled: true);
    }

    internal readonly record struct RespValue
    {
        private RespValue(RespKind kind, string? text, byte[]? bulk, int bulkLength, bool bulkIsPooled, long integer, RespValue[]? array, int arrayLength, bool arrayIsPooled)
        {
            Kind = kind;
            Text = text;
            Bulk = bulk;
            BulkLength = bulkLength;
            BulkIsPooled = bulkIsPooled;
            IntegerValue = integer;
            ArrayItems = array;
            ArrayLength = arrayLength;
            ArrayIsPooled = arrayIsPooled;
        }

        public RespKind Kind { get; }
        public string? Text { get; }
        public byte[]? Bulk { get; }
        public int BulkLength { get; }
        public bool BulkIsPooled { get; }
        public long IntegerValue { get; }
        public RespValue[]? ArrayItems { get; }
        public int ArrayLength { get; }
        public bool ArrayIsPooled { get; }

        public static RespValue SimpleString(string s) => new(RespKind.SimpleString, s, null, 0, false, 0, null, 0, false);
        public static RespValue Error(string s) => new(RespKind.Error, s, null, 0, false, 0, null, 0, false);
        public static RespValue Integer(long v) => new(RespKind.Integer, null, null, 0, false, v, null, 0, false);
        public static RespValue BulkString(byte[] bytes, int length, bool pooled) => new(RespKind.BulkString, null, bytes, length, pooled, 0, null, 0, false);
        public static RespValue NullBulkString() => new(RespKind.NullBulkString, null, null, 0, false, 0, null, 0, false);
        public static RespValue Array(RespValue[] items, int length, bool pooled = false) => new(RespKind.Array, null, null, 0, false, 0, items, length, pooled);
        public static RespValue NullArray() => new(RespKind.NullArray, null, null, 0, false, 0, null, 0, false);
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
