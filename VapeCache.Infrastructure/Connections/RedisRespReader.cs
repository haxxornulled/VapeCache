using System.Buffers;
using System.Globalization;
using System.Text;

namespace VapeCache.Infrastructure.Connections;

internal static class RedisRespReader
{
    internal static readonly string OkSimpleString = "OK";
    internal static readonly string PongSimpleString = "PONG";
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

        if ((value.Kind == RespKind.Array || value.Kind == RespKind.Push) &&
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
        Array.Clear(array, 0, array.Length);

        // PERFORMANCE FIX P2-2: Use ThreadStatic cache for lock-free fast path
        // Only cache one array per thread to keep TLS overhead minimal
        if (array.Length <= MaxCachedArrayLength && _tlsCachedArray is null)
        {
            _tlsCachedArray = array;
            return;
        }

        ArrayPool<RespValue>.Shared.Return(array, clearArray: false);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public static ValueTask<RespValue> ReadAsync(
        Stream stream,
        CancellationToken ct,
        int maxBulkStringBytes = 16 * 1024 * 1024,
        int maxArrayDepth = 64)
    {
        return ReadAsyncInternal(stream, ct, maxBulkStringBytes, maxArrayDepth, currentArrayDepth: 0);
    }

    private static async ValueTask<RespValue> ReadAsyncInternal(
        Stream stream,
        CancellationToken ct,
        int maxBulkStringBytes,
        int maxArrayDepth,
        int currentArrayDepth)
    {
        var prefix = await ReadByteAsync(stream, ct).ConfigureAwait(false);
        return prefix switch
        {
            (byte)'+' => await ReadSimpleStringValueAsync(stream, ct).ConfigureAwait(false),
            (byte)'-' => RespValue.Error(await RedisRespProtocol.ReadLineAsync(stream, ct).ConfigureAwait(false)),
            (byte)':' => RespValue.Integer(ReadInt64(await RedisRespProtocol.ReadLineAsync(stream, ct).ConfigureAwait(false))),
            (byte)'$' => await ReadBulkStringAsync(stream, ct, maxBulkStringBytes).ConfigureAwait(false),
            (byte)'*' => await ReadArrayAsync(stream, ct, maxBulkStringBytes, maxArrayDepth, currentArrayDepth).ConfigureAwait(false),
            (byte)'_' => await ReadNullAsync(stream, ct).ConfigureAwait(false),
            (byte)',' => RespValue.SimpleString(await RedisRespProtocol.ReadLineAsync(stream, ct).ConfigureAwait(false)),
            (byte)'#' => await ReadBooleanAsync(stream, ct).ConfigureAwait(false),
            (byte)'(' => RespValue.SimpleString(await RedisRespProtocol.ReadLineAsync(stream, ct).ConfigureAwait(false)),
            (byte)'=' => await ReadVerbatimStringAsync(stream, ct, maxBulkStringBytes).ConfigureAwait(false),
            (byte)'!' => await ReadBlobErrorAsync(stream, ct, maxBulkStringBytes).ConfigureAwait(false),
            (byte)'~' => await ReadArrayLikeAsync(stream, ct, maxBulkStringBytes, maxArrayDepth, currentArrayDepth, isPush: false).ConfigureAwait(false),
            (byte)'>' => await ReadArrayLikeAsync(stream, ct, maxBulkStringBytes, maxArrayDepth, currentArrayDepth, isPush: true).ConfigureAwait(false),
            (byte)'%' => await ReadMapAsArrayAsync(stream, ct, maxBulkStringBytes, maxArrayDepth, currentArrayDepth).ConfigureAwait(false),
            (byte)'|' => await ReadAttributeWrappedAsync(stream, ct, maxBulkStringBytes, maxArrayDepth, currentArrayDepth).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported RESP type: {(char)prefix}")
        };
    }

    private static async ValueTask<RespValue> ReadSimpleStringValueAsync(Stream stream, CancellationToken ct)
    {
        var s = await RedisRespProtocol.ReadLineAsync(stream, ct).ConfigureAwait(false);
        if (ReferenceEquals(s, OkSimpleString) || s == OkSimpleString)
            return RespValue.SimpleString(OkSimpleString);

        if (ReferenceEquals(s, PongSimpleString) || s == PongSimpleString)
            return RespValue.SimpleString(PongSimpleString);

        return RespValue.SimpleString(s);
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

    private static async ValueTask<RespValue> ReadBulkStringAsync(Stream stream, CancellationToken ct, int maxBulkStringBytes)
    {
        var header = await RedisRespProtocol.ReadLineAsync(stream, ct).ConfigureAwait(false);
        if (!int.TryParse(header, out var len))
            throw new InvalidOperationException($"Invalid bulk length: {header}");

        if (len == -1)
            return RespValue.NullBulkString();

        if (len < 0)
            throw new InvalidOperationException($"Invalid bulk length: {header}");

        if (maxBulkStringBytes >= 0 && len > maxBulkStringBytes)
            throw new InvalidOperationException($"Bulk string size {len} bytes exceeds maximum allowed size of {maxBulkStringBytes} bytes. Possible DoS attack or misconfigured server.");

        // The payload is fully overwritten by the read loop, so zero-init is unnecessary work here.
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

    private static async ValueTask<RespValue> ReadArrayAsync(
        Stream stream,
        CancellationToken ct,
        int maxBulkStringBytes,
        int maxArrayDepth,
        int currentArrayDepth)
        => await ReadArrayLikeAsync(stream, ct, maxBulkStringBytes, maxArrayDepth, currentArrayDepth, isPush: false).ConfigureAwait(false);

    private static async ValueTask<RespValue> ReadArrayLikeAsync(
        Stream stream,
        CancellationToken ct,
        int maxBulkStringBytes,
        int maxArrayDepth,
        int currentArrayDepth,
        bool isPush)
    {
        var header = await RedisRespProtocol.ReadLineAsync(stream, ct).ConfigureAwait(false);
        if (!int.TryParse(header, out var len))
            throw new InvalidOperationException($"Invalid array length: {header}");

        if (len == -1)
            return RespValue.NullArray();

        if (len < 0)
            throw new InvalidOperationException($"Invalid array length: {header}");

        var nextDepth = currentArrayDepth + 1;
        if (maxArrayDepth >= 0 && nextDepth > maxArrayDepth)
            throw new InvalidOperationException($"Array nesting depth {nextDepth} exceeds maximum allowed depth of {maxArrayDepth}. Possible stack overflow attack.");

        var items = RentArray(len);
        var filled = 0;
        try
        {
            for (var i = 0; i < len; i++)
            {
                items[i] = await ReadAsyncInternal(stream, ct, maxBulkStringBytes, maxArrayDepth, nextDepth).ConfigureAwait(false);
                filled++;
            }

            return isPush
                ? RespValue.Push(items, len, pooled: true)
                : RespValue.Array(items, len, pooled: true);
        }
        catch
        {
            for (var i = 0; i < filled; i++)
                ReturnBuffers(items[i]);
            ReturnArray(items, len);
            throw;
        }
    }

    private static async ValueTask<RespValue> ReadMapAsArrayAsync(
        Stream stream,
        CancellationToken ct,
        int maxBulkStringBytes,
        int maxArrayDepth,
        int currentArrayDepth)
    {
        var header = await RedisRespProtocol.ReadLineAsync(stream, ct).ConfigureAwait(false);
        if (!int.TryParse(header, out var pairCount))
            throw new InvalidOperationException($"Invalid map length: {header}");

        if (pairCount == -1)
            return RespValue.NullArray();

        if (pairCount < 0)
            throw new InvalidOperationException($"Invalid map length: {header}");

        return await ReadAggregateItemsAsync(
            stream,
            ct,
            elementCount: checked(pairCount * 2),
            maxBulkStringBytes,
            maxArrayDepth,
            currentArrayDepth,
            asPush: false).ConfigureAwait(false);
    }

    private static async ValueTask<RespValue> ReadAttributeWrappedAsync(
        Stream stream,
        CancellationToken ct,
        int maxBulkStringBytes,
        int maxArrayDepth,
        int currentArrayDepth)
    {
        var header = await RedisRespProtocol.ReadLineAsync(stream, ct).ConfigureAwait(false);
        if (!int.TryParse(header, out var pairCount))
            throw new InvalidOperationException($"Invalid attribute length: {header}");

        if (pairCount < 0)
            return await ReadAsyncInternal(stream, ct, maxBulkStringBytes, maxArrayDepth, currentArrayDepth).ConfigureAwait(false);

        var nextDepth = currentArrayDepth + 1;
        if (maxArrayDepth >= 0 && nextDepth > maxArrayDepth)
            throw new InvalidOperationException($"Array nesting depth {nextDepth} exceeds maximum allowed depth of {maxArrayDepth}. Possible stack overflow attack.");

        for (var i = 0; i < pairCount * 2; i++)
        {
            var attr = await ReadAsyncInternal(stream, ct, maxBulkStringBytes, maxArrayDepth, nextDepth).ConfigureAwait(false);
            ReturnBuffers(attr);
        }

        return await ReadAsyncInternal(stream, ct, maxBulkStringBytes, maxArrayDepth, currentArrayDepth).ConfigureAwait(false);
    }

    private static async ValueTask<RespValue> ReadAggregateItemsAsync(
        Stream stream,
        CancellationToken ct,
        int elementCount,
        int maxBulkStringBytes,
        int maxArrayDepth,
        int currentArrayDepth,
        bool asPush)
    {
        if (elementCount < 0)
            throw new InvalidOperationException($"Invalid aggregate length: {elementCount}");

        var nextDepth = currentArrayDepth + 1;
        if (maxArrayDepth >= 0 && nextDepth > maxArrayDepth)
            throw new InvalidOperationException($"Array nesting depth {nextDepth} exceeds maximum allowed depth of {maxArrayDepth}. Possible stack overflow attack.");

        var items = RentArray(elementCount);
        var filled = 0;
        try
        {
            for (var i = 0; i < elementCount; i++)
            {
                items[i] = await ReadAsyncInternal(stream, ct, maxBulkStringBytes, maxArrayDepth, nextDepth).ConfigureAwait(false);
                filled++;
            }

            return asPush
                ? RespValue.Push(items, elementCount, pooled: true)
                : RespValue.Array(items, elementCount, pooled: true);
        }
        catch
        {
            for (var i = 0; i < filled; i++)
                ReturnBuffers(items[i]);
            ReturnArray(items, elementCount);
            throw;
        }
    }

    private static async ValueTask<RespValue> ReadNullAsync(Stream stream, CancellationToken ct)
    {
        var line = await RedisRespProtocol.ReadLineAsync(stream, ct).ConfigureAwait(false);
        if (line.Length != 0)
            throw new InvalidOperationException($"Invalid null response payload: {line}");
        return RespValue.NullBulkString();
    }

    private static async ValueTask<RespValue> ReadBooleanAsync(Stream stream, CancellationToken ct)
    {
        var line = await RedisRespProtocol.ReadLineAsync(stream, ct).ConfigureAwait(false);
        if (line.Length == 1 && (line[0] == 't' || line[0] == 'T'))
            return RespValue.Integer(1);
        if (line.Length == 1 && (line[0] == 'f' || line[0] == 'F'))
            return RespValue.Integer(0);

        throw new InvalidOperationException($"Invalid boolean response: {line}");
    }

    private static async ValueTask<RespValue> ReadVerbatimStringAsync(Stream stream, CancellationToken ct, int maxBulkStringBytes)
    {
        var value = await ReadBulkStringAsync(stream, ct, maxBulkStringBytes).ConfigureAwait(false);
        if (value.Kind != RespKind.BulkString || value.Bulk is null)
            return value;

        if (value.BulkLength <= 4 || value.Bulk[3] != (byte)':')
            return value;

        var payloadLen = value.BulkLength - 4;
        var payload = GC.AllocateUninitializedArray<byte>(payloadLen);
        Buffer.BlockCopy(value.Bulk, 4, payload, 0, payloadLen);
        return RespValue.BulkString(payload, payloadLen, pooled: false);
    }

    private static async ValueTask<RespValue> ReadBlobErrorAsync(Stream stream, CancellationToken ct, int maxBulkStringBytes)
    {
        var bulk = await ReadBulkStringAsync(stream, ct, maxBulkStringBytes).ConfigureAwait(false);
        return bulk.Kind switch
        {
            RespKind.NullBulkString => RespValue.Error(string.Empty),
            RespKind.BulkString when bulk.Bulk is not null => RespValue.Error(Encoding.UTF8.GetString(bulk.Bulk, 0, bulk.BulkLength)),
            _ => throw new InvalidOperationException("Invalid blob error payload.")
        };
    }

    internal readonly struct RespValue
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

        /// <summary>
        /// Executes value.
        /// </summary>
        public static RespValue SimpleString(string s) => new(RespKind.SimpleString, s, null, 0, false, 0, null, 0, false);
        /// <summary>
        /// Executes value.
        /// </summary>
        public static RespValue Error(string s) => new(RespKind.Error, s, null, 0, false, 0, null, 0, false);
        /// <summary>
        /// Executes value.
        /// </summary>
        public static RespValue Integer(long v) => new(RespKind.Integer, null, null, 0, false, v, null, 0, false);
        /// <summary>
        /// Executes value.
        /// </summary>
        public static RespValue BulkString(byte[] bytes, int length, bool pooled) => new(RespKind.BulkString, null, bytes, length, pooled, 0, null, 0, false);
        /// <summary>
        /// Executes value.
        /// </summary>
        public static RespValue NullBulkString() => new(RespKind.NullBulkString, null, null, 0, false, 0, null, 0, false);
        /// <summary>
        /// Executes value.
        /// </summary>
        public static RespValue Array(RespValue[] items, int length, bool pooled = false) => new(RespKind.Array, null, null, 0, false, 0, items, length, pooled);
        /// <summary>
        /// Executes value.
        /// </summary>
        public static RespValue NullArray() => new(RespKind.NullArray, null, null, 0, false, 0, null, 0, false);
        /// <summary>
        /// Executes value.
        /// </summary>
        public static RespValue Push(RespValue[] items, int length, bool pooled = false) => new(RespKind.Push, null, null, 0, false, 0, items, length, pooled);
    }

    internal enum RespKind
    {
        SimpleString,
        Error,
        Integer,
        BulkString,
        NullBulkString,
        Array,
        NullArray,
        Push
    }
}
