using System.Buffers;
using System.Globalization;

namespace VapeCache.Infrastructure.Connections;

internal static class RedisRespReader
{
    internal static readonly string OkSimpleString = "OK";
    private const int MaxCachedArrayLength = 16;

    // PERFORMANCE FIX P2-2: Replace global lock with ThreadStatic cache to eliminate contention
    // Each thread maintains its own cache of small RespValue arrays
    [ThreadStatic] private static RespValue[]? _tlsCachedArray;

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
        // PERFORMANCE FIX P2-2: Use ThreadStatic cache for lock-free fast path
        // Only small arrays (≤16 elements) are cached, larger ones go directly to ArrayPool
        if (length <= MaxCachedArrayLength && _tlsCachedArray is { } cached && cached.Length >= length)
        {
            _tlsCachedArray = null;
            return cached;
        }

        return ArrayPool<RespValue>.Shared.Rent(length);
    }

    internal static void ReturnArray(RespValue[] array, int length)
    {
        // Clear references to avoid holding onto nested pooled buffers.
        Array.Clear(array, 0, Math.Min(length, array.Length));

        // PERFORMANCE FIX P2-2: Use ThreadStatic cache for lock-free fast path
        // Only cache one array per thread to keep TLS overhead minimal
        if (length <= MaxCachedArrayLength && _tlsCachedArray is null)
        {
            _tlsCachedArray = array;
            return;
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

        // Use GC.AllocateArray instead of AllocateUninitializedArray to ensure zero-initialization
        // This prevents potential garbage data in the array that can cause JSON deserialization errors
        var buf = new byte[len];
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

    internal sealed class RespValue
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
