using System.Buffers;
using System.Globalization;
using System.Text;

namespace VapeCache.Infrastructure.Connections;

internal static class RedisRespReader
{
    internal static readonly string OkSimpleString = "OK";
    internal static readonly string PongSimpleString = "PONG";
    private const int MaxCachedArrayLength = 16;
    private const int InitialLineBufferSize = 256;
    private const int RetainedLineBufferLimit = 4096;

    // PERFORMANCE FIX P2-2: Replace global lock with ThreadStatic cache to eliminate contention
    // Each thread maintains its own cache of small RespValue arrays
    [ThreadStatic] private static RespValue[]? _tlsCachedArray;
    [ThreadStatic] private static StreamReadScratch? _tlsStreamScratch;

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
        return ReadWithScratchAsync(stream, ct, maxBulkStringBytes, maxArrayDepth);
    }

    private static async ValueTask<RespValue> ReadWithScratchAsync(
        Stream stream,
        CancellationToken ct,
        int maxBulkStringBytes,
        int maxArrayDepth)
    {
        var scratch = RentStreamScratch();
        try
        {
            return await ReadAsyncInternal(
                stream,
                ct,
                maxBulkStringBytes,
                maxArrayDepth,
                currentArrayDepth: 0,
                scratch).ConfigureAwait(false);
        }
        finally
        {
            ReturnStreamScratch(scratch);
        }
    }

    private static async ValueTask<RespValue> ReadAsyncInternal(
        Stream stream,
        CancellationToken ct,
        int maxBulkStringBytes,
        int maxArrayDepth,
        int currentArrayDepth,
        StreamReadScratch scratch)
    {
        var prefix = await ReadByteAsync(stream, ct, scratch).ConfigureAwait(false);
        return prefix switch
        {
            (byte)'+' => await ReadSimpleStringValueAsync(stream, ct, scratch).ConfigureAwait(false),
            (byte)'-' => RespValue.Error(await ReadLineAsync(stream, ct, scratch).ConfigureAwait(false)),
            (byte)':' => RespValue.Integer(ReadInt64(await ReadLineAsync(stream, ct, scratch).ConfigureAwait(false))),
            (byte)'$' => await ReadBulkStringAsync(stream, ct, maxBulkStringBytes, scratch).ConfigureAwait(false),
            (byte)'*' => await ReadArrayAsync(stream, ct, maxBulkStringBytes, maxArrayDepth, currentArrayDepth, scratch).ConfigureAwait(false),
            (byte)'_' => await ReadNullAsync(stream, ct, scratch).ConfigureAwait(false),
            (byte)',' => RespValue.SimpleString(await ReadLineAsync(stream, ct, scratch).ConfigureAwait(false)),
            (byte)'#' => await ReadBooleanAsync(stream, ct, scratch).ConfigureAwait(false),
            (byte)'(' => RespValue.SimpleString(await ReadLineAsync(stream, ct, scratch).ConfigureAwait(false)),
            (byte)'=' => await ReadVerbatimStringAsync(stream, ct, maxBulkStringBytes, scratch).ConfigureAwait(false),
            (byte)'!' => await ReadBlobErrorAsync(stream, ct, maxBulkStringBytes, scratch).ConfigureAwait(false),
            (byte)'~' => await ReadArrayLikeAsync(stream, ct, maxBulkStringBytes, maxArrayDepth, currentArrayDepth, scratch, isPush: false).ConfigureAwait(false),
            (byte)'>' => await ReadArrayLikeAsync(stream, ct, maxBulkStringBytes, maxArrayDepth, currentArrayDepth, scratch, isPush: true).ConfigureAwait(false),
            (byte)'%' => await ReadMapAsArrayAsync(stream, ct, maxBulkStringBytes, maxArrayDepth, currentArrayDepth, scratch).ConfigureAwait(false),
            (byte)'|' => await ReadAttributeWrappedAsync(stream, ct, maxBulkStringBytes, maxArrayDepth, currentArrayDepth, scratch).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported RESP type: {(char)prefix}")
        };
    }

    private static async ValueTask<RespValue> ReadSimpleStringValueAsync(Stream stream, CancellationToken ct, StreamReadScratch scratch)
    {
        var s = await ReadLineAsync(stream, ct, scratch).ConfigureAwait(false);
        if (ReferenceEquals(s, OkSimpleString) || s == OkSimpleString)
            return RespValue.SimpleString(OkSimpleString);

        if (ReferenceEquals(s, PongSimpleString) || s == PongSimpleString)
            return RespValue.SimpleString(PongSimpleString);

        return RespValue.SimpleString(s);
    }

    private static async ValueTask<byte> ReadByteAsync(Stream stream, CancellationToken ct, StreamReadScratch scratch)
    {
        await ReadExactAsync(stream, scratch.SingleByte.AsMemory(0, 1), ct).ConfigureAwait(false);
        return scratch.SingleByte[0];
    }

    private static long ReadInt64(string line)
    {
        if (!long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException($"Invalid integer response: {line}");
        return value;
    }

    private static async ValueTask<RespValue> ReadBulkStringAsync(Stream stream, CancellationToken ct, int maxBulkStringBytes, StreamReadScratch scratch)
    {
        var header = await ReadLineAsync(stream, ct, scratch).ConfigureAwait(false);
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
        await ReadExactAsync(stream, scratch.Crlf.AsMemory(0, 2), ct).ConfigureAwait(false);

        return RespValue.BulkString(buf, len, pooled: false);
    }

    private static async ValueTask<RespValue> ReadArrayAsync(
        Stream stream,
        CancellationToken ct,
        int maxBulkStringBytes,
        int maxArrayDepth,
        int currentArrayDepth,
        StreamReadScratch scratch)
        => await ReadArrayLikeAsync(stream, ct, maxBulkStringBytes, maxArrayDepth, currentArrayDepth, scratch, isPush: false).ConfigureAwait(false);

    private static async ValueTask<RespValue> ReadArrayLikeAsync(
        Stream stream,
        CancellationToken ct,
        int maxBulkStringBytes,
        int maxArrayDepth,
        int currentArrayDepth,
        StreamReadScratch scratch,
        bool isPush)
    {
        var header = await ReadLineAsync(stream, ct, scratch).ConfigureAwait(false);
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
                items[i] = await ReadAsyncInternal(stream, ct, maxBulkStringBytes, maxArrayDepth, nextDepth, scratch).ConfigureAwait(false);
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
        int currentArrayDepth,
        StreamReadScratch scratch)
    {
        var header = await ReadLineAsync(stream, ct, scratch).ConfigureAwait(false);
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
            scratch,
            asPush: false).ConfigureAwait(false);
    }

    private static async ValueTask<RespValue> ReadAttributeWrappedAsync(
        Stream stream,
        CancellationToken ct,
        int maxBulkStringBytes,
        int maxArrayDepth,
        int currentArrayDepth,
        StreamReadScratch scratch)
    {
        var header = await ReadLineAsync(stream, ct, scratch).ConfigureAwait(false);
        if (!int.TryParse(header, out var pairCount))
            throw new InvalidOperationException($"Invalid attribute length: {header}");

        if (pairCount < 0)
            return await ReadAsyncInternal(stream, ct, maxBulkStringBytes, maxArrayDepth, currentArrayDepth, scratch).ConfigureAwait(false);

        var nextDepth = currentArrayDepth + 1;
        if (maxArrayDepth >= 0 && nextDepth > maxArrayDepth)
            throw new InvalidOperationException($"Array nesting depth {nextDepth} exceeds maximum allowed depth of {maxArrayDepth}. Possible stack overflow attack.");

        for (var i = 0; i < pairCount * 2; i++)
        {
            var attr = await ReadAsyncInternal(stream, ct, maxBulkStringBytes, maxArrayDepth, nextDepth, scratch).ConfigureAwait(false);
            ReturnBuffers(attr);
        }

        return await ReadAsyncInternal(stream, ct, maxBulkStringBytes, maxArrayDepth, currentArrayDepth, scratch).ConfigureAwait(false);
    }

    private static async ValueTask<RespValue> ReadAggregateItemsAsync(
        Stream stream,
        CancellationToken ct,
        int elementCount,
        int maxBulkStringBytes,
        int maxArrayDepth,
        int currentArrayDepth,
        StreamReadScratch scratch,
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
                items[i] = await ReadAsyncInternal(stream, ct, maxBulkStringBytes, maxArrayDepth, nextDepth, scratch).ConfigureAwait(false);
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

    private static async ValueTask<RespValue> ReadNullAsync(Stream stream, CancellationToken ct, StreamReadScratch scratch)
    {
        var line = await ReadLineAsync(stream, ct, scratch).ConfigureAwait(false);
        if (line.Length != 0)
            throw new InvalidOperationException($"Invalid null response payload: {line}");
        return RespValue.NullBulkString();
    }

    private static async ValueTask<RespValue> ReadBooleanAsync(Stream stream, CancellationToken ct, StreamReadScratch scratch)
    {
        var line = await ReadLineAsync(stream, ct, scratch).ConfigureAwait(false);
        if (line.Length == 1 && (line[0] == 't' || line[0] == 'T'))
            return RespValue.Integer(1);
        if (line.Length == 1 && (line[0] == 'f' || line[0] == 'F'))
            return RespValue.Integer(0);

        throw new InvalidOperationException($"Invalid boolean response: {line}");
    }

    private static async ValueTask<RespValue> ReadVerbatimStringAsync(Stream stream, CancellationToken ct, int maxBulkStringBytes, StreamReadScratch scratch)
    {
        var value = await ReadBulkStringAsync(stream, ct, maxBulkStringBytes, scratch).ConfigureAwait(false);
        if (value.Kind != RespKind.BulkString || value.Bulk is null)
            return value;

        if (value.BulkLength <= 4 || value.Bulk[3] != (byte)':')
            return value;

        var payloadLen = value.BulkLength - 4;
        var payload = GC.AllocateUninitializedArray<byte>(payloadLen);
        Buffer.BlockCopy(value.Bulk, 4, payload, 0, payloadLen);
        return RespValue.BulkString(payload, payloadLen, pooled: false);
    }

    private static async ValueTask<RespValue> ReadBlobErrorAsync(Stream stream, CancellationToken ct, int maxBulkStringBytes, StreamReadScratch scratch)
    {
        var bulk = await ReadBulkStringAsync(stream, ct, maxBulkStringBytes, scratch).ConfigureAwait(false);
        return bulk.Kind switch
        {
            RespKind.NullBulkString => RespValue.Error(string.Empty),
            RespKind.BulkString when bulk.Bulk is not null => RespValue.Error(Encoding.UTF8.GetString(bulk.Bulk, 0, bulk.BulkLength)),
            _ => throw new InvalidOperationException("Invalid blob error payload.")
        };
    }

    private static async ValueTask<string> ReadLineAsync(Stream stream, CancellationToken ct, StreamReadScratch scratch)
    {
        var buffer = scratch.LineBuffer;
        var count = 0;
        while (true)
        {
            if (count == buffer.Length)
            {
                var bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                Buffer.BlockCopy(buffer, 0, bigger, 0, count);
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = bigger;
                scratch.LineBuffer = buffer;
            }

            var read = await stream.ReadAsync(buffer.AsMemory(count, 1), ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException();

            count++;
            if (count >= 2 && buffer[count - 2] == (byte)'\r' && buffer[count - 1] == (byte)'\n')
                return Encoding.UTF8.GetString(buffer, 0, count - 2);
        }
    }

    private static async ValueTask ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.Slice(read), ct).ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException();

            read += n;
        }
    }

    private static StreamReadScratch RentStreamScratch()
    {
        var scratch = _tlsStreamScratch;
        if (scratch is not null)
        {
            _tlsStreamScratch = null;
            return scratch;
        }

        return new StreamReadScratch(
            ArrayPool<byte>.Shared.Rent(1),
            ArrayPool<byte>.Shared.Rent(2),
            ArrayPool<byte>.Shared.Rent(InitialLineBufferSize));
    }

    private static void ReturnStreamScratch(StreamReadScratch scratch)
    {
        scratch.TrimLineBuffer();
        if (_tlsStreamScratch is null)
        {
            _tlsStreamScratch = scratch;
            return;
        }

        scratch.Dispose();
    }

    private sealed class StreamReadScratch : IDisposable
    {
        public StreamReadScratch(byte[] singleByte, byte[] crlf, byte[] lineBuffer)
        {
            SingleByte = singleByte;
            Crlf = crlf;
            LineBuffer = lineBuffer;
        }

        public byte[] SingleByte { get; private set; }
        public byte[] Crlf { get; private set; }
        public byte[] LineBuffer { get; set; }

        public void TrimLineBuffer()
        {
            if (LineBuffer.Length <= RetainedLineBufferLimit)
                return;

            ArrayPool<byte>.Shared.Return(LineBuffer);
            LineBuffer = ArrayPool<byte>.Shared.Rent(InitialLineBufferSize);
        }

        public void Dispose()
        {
            if (SingleByte.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(SingleByte);
                SingleByte = Array.Empty<byte>();
            }

            if (Crlf.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(Crlf);
                Crlf = Array.Empty<byte>();
            }

            if (LineBuffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(LineBuffer);
                LineBuffer = Array.Empty<byte>();
            }
        }
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
