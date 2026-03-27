using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisRespReaderState : IAsyncDisposable
{
    private readonly Stream _stream;
    private byte[] _buffer;
    private int _pos;
    private int _len;
    private int _disposed;
    private int _activeReaders;
    private int _bufferReturned;
    private int _readCallDepth;
    private long _totalBytesRead;
    private readonly bool _useUnsafeFastPath;
    private readonly int _maxBulkStringBytes;
    private readonly int _maxArrayDepth;
    private int _currentArrayDepth;
    private readonly Action<int>? _onBytesRead;

    public RedisRespReaderState(
        Stream stream,
        int bufferSize = 8192,
        bool useUnsafeFastPath = false,
        int maxBulkStringBytes = 16 * 1024 * 1024,
        int maxArrayDepth = 64,
        Action<int>? onBytesRead = null)
    {
        _stream = stream;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(256, bufferSize));
        _useUnsafeFastPath = useUnsafeFastPath;
        _maxBulkStringBytes = maxBulkStringBytes;
        _maxArrayDepth = maxArrayDepth;
        _currentArrayDepth = 0;
        _onBytesRead = onBytesRead;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<RedisRespReader.RespValue> ReadAsync(CancellationToken ct) => ReadAsync(poolBulk: false, ct);

    /// <summary>
    /// Executes value.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<RedisRespReader.RespValue> ReadAsync(bool poolBulk, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        Interlocked.Increment(ref _activeReaders);
        if (Volatile.Read(ref _disposed) == 1)
        {
            CompleteRead();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        }

        var depth = Interlocked.Increment(ref _readCallDepth);
        var isTopLevelRead = depth == 1;
        var parseStartBytes = isTopLevelRead ? GetConsumedByteCount() : 0L;
        var parseStartTicks = isTopLevelRead ? Stopwatch.GetTimestamp() : 0L;

        try
        {
            var prefix = await ReadByteAsync(ct).ConfigureAwait(false);
            var value = prefix switch
            {
                (byte)'+' => RedisRespReader.RespValue.SimpleString(await ReadSimpleStringAsync(ct).ConfigureAwait(false)),
                (byte)'-' => RedisRespReader.RespValue.Error(await ReadLineAsync(ct).ConfigureAwait(false)),
                (byte)':' => RedisRespReader.RespValue.Integer(await ReadInt64LineAsync(ct).ConfigureAwait(false)),
                (byte)'$' => await ReadBulkStringAsync(poolBulk, ct).ConfigureAwait(false),
                (byte)'*' => await ReadArrayAsync(poolBulk, ct).ConfigureAwait(false),
                (byte)'_' => await ReadNullAsync(ct).ConfigureAwait(false),
                (byte)',' => RedisRespReader.RespValue.SimpleString(await ReadLineAsync(ct).ConfigureAwait(false)),
                (byte)'#' => await ReadBooleanAsync(ct).ConfigureAwait(false),
                (byte)'(' => RedisRespReader.RespValue.SimpleString(await ReadLineAsync(ct).ConfigureAwait(false)),
                (byte)'=' => await ReadVerbatimStringAsync(poolBulk, ct).ConfigureAwait(false),
                (byte)'!' => await ReadBlobErrorAsync(ct).ConfigureAwait(false),
                (byte)'~' => await ReadSetAsync(poolBulk, ct).ConfigureAwait(false),
                (byte)'>' => await ReadPushAsync(poolBulk, ct).ConfigureAwait(false),
                (byte)'%' => await ReadMapAsArrayAsync(poolBulk, ct).ConfigureAwait(false),
                (byte)'|' => await ReadAttributeWrappedAsync(poolBulk, ct).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unsupported RESP type: {(char)prefix}")
            };

            if (isTopLevelRead)
            {
                var parsedBytes = GetConsumedByteCount() - parseStartBytes;
                var elapsedTicks = Stopwatch.GetTimestamp() - parseStartTicks;
                RedisTelemetry.RecordParserFrame(parsedBytes, elapsedTicks);
            }

            return value;
        }
        finally
        {
            Interlocked.Decrement(ref _readCallDepth);
            CompleteRead();
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<RedisRespReader.RespValue> ReadCountAsync(RedisResponseMode responseMode, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        Interlocked.Increment(ref _activeReaders);
        if (Volatile.Read(ref _disposed) == 1)
        {
            CompleteRead();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        }

        var depth = Interlocked.Increment(ref _readCallDepth);
        var isTopLevelRead = depth == 1;
        var parseStartBytes = isTopLevelRead ? GetConsumedByteCount() : 0L;
        var parseStartTicks = isTopLevelRead ? Stopwatch.GetTimestamp() : 0L;

        try
        {
            var value = await ReadCountInternalAsync(responseMode, ct).ConfigureAwait(false);

            if (isTopLevelRead)
            {
                var parsedBytes = GetConsumedByteCount() - parseStartBytes;
                var elapsedTicks = Stopwatch.GetTimestamp() - parseStartTicks;
                RedisTelemetry.RecordParserFrame(parsedBytes, elapsedTicks);
            }

            return value;
        }
        finally
        {
            Interlocked.Decrement(ref _readCallDepth);
            CompleteRead();
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<byte> ReadByteAsync(CancellationToken ct)
    {
        if (_pos >= _len)
            await FillAsync(ct).ConfigureAwait(false);
        return _buffer[_pos++];
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<string> ReadSimpleStringAsync(CancellationToken ct)
    {
        if (_pos >= _len)
            await FillAsync(ct).ConfigureAwait(false);

        var span = _buffer.AsSpan(_pos, _len - _pos);
        var lf = span.IndexOf((byte)'\n');
        if (lf < 0)
            return await ReadLineAsync(ct).ConfigureAwait(false);

        var lineLen = lf > 0 && span[lf - 1] == (byte)'\r' ? lf - 1 : lf;
        if (lineLen == 2 && span[0] == (byte)'O' && span[1] == (byte)'K')
        {
            _pos += lf + 1;
            return RedisRespReader.OkSimpleString;
        }

        if (lineLen == 4 &&
            span[0] == (byte)'P' &&
            span[1] == (byte)'O' &&
            span[2] == (byte)'N' &&
            span[3] == (byte)'G')
        {
            _pos += lf + 1;
            return RedisRespReader.PongSimpleString;
        }

        var value = Encoding.UTF8.GetString(_buffer, _pos, lineLen);
        _pos += lf + 1;
        return value;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask FillAsync(CancellationToken ct)
    {
        _pos = 0;
        _len = 0;
        var read = await _stream.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
        if (read == 0) throw new EndOfStreamException();
        RedisTelemetry.BytesReceived.Add(read);
        _onBytesRead?.Invoke(read);
        _totalBytesRead += read;
        _len = read;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private long GetConsumedByteCount()
        => _totalBytesRead - (_len - _pos);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<string> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_pos >= _len)
                await FillAsync(ct).ConfigureAwait(false);

            var span = _buffer.AsSpan(_pos, _len - _pos);
            var lf = span.IndexOf((byte)'\n');
            if (lf >= 0)
            {
                var lineLen = lf > 0 && span[lf - 1] == (byte)'\r' ? lf - 1 : lf;
                var s = Encoding.UTF8.GetString(_buffer, _pos, lineLen);
                _pos += lf + 1;
                return s;
            }

            // Line spans multiple fills: accumulate remaining bytes then continue.
            byte[]? rented = null;
            try
            {
                rented = ArrayPool<byte>.Shared.Rent(512);
                var total = 0;

                while (true)
                {
                    var remaining = _len - _pos;
                    if (remaining > 0)
                    {
                        if (total + remaining > rented.Length)
                        {
                            var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(rented.Length * 2, total + remaining));
                            Buffer.BlockCopy(rented, 0, bigger, 0, total);
                            ArrayPool<byte>.Shared.Return(rented);
                            rented = bigger;
                        }

                        Buffer.BlockCopy(_buffer, _pos, rented, total, remaining);
                        total += remaining;
                        _pos = _len;
                    }

                    await FillAsync(ct).ConfigureAwait(false);
                    span = _buffer.AsSpan(_pos, _len - _pos);
                    lf = span.IndexOf((byte)'\n');
                    if (lf < 0)
                        continue;

                    var take = lf + 1;
                    if (total + take > rented.Length)
                    {
                        var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(rented.Length * 2, total + take));
                        Buffer.BlockCopy(rented, 0, bigger, 0, total);
                        ArrayPool<byte>.Shared.Return(rented);
                        rented = bigger;
                    }

                    Buffer.BlockCopy(_buffer, _pos, rented, total, take);
                    total += take;
                    _pos += take;

                    // strip trailing CRLF or LF
                    var end = total;
                    if (end >= 2 && rented[end - 2] == (byte)'\r' && rented[end - 1] == (byte)'\n')
                        end -= 2;
                    else if (end >= 1 && rented[end - 1] == (byte)'\n')
                        end -= 1;

                    return Encoding.UTF8.GetString(rented, 0, end);
                }
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RedisRespReader.RespValue> ReadBulkStringAsync(bool poolBulk, CancellationToken ct)
    {
        var len = await ReadInt32LineAsync(ct).ConfigureAwait(false);

        if (len == -1)
            return RedisRespReader.RespValue.NullBulkString();

        if (len < 0)
            throw new InvalidOperationException($"Invalid bulk length: {len}");

        // CRITICAL: Prevent DoS attacks from malicious Redis server sending huge bulk strings
        if (_maxBulkStringBytes >= 0 && len > _maxBulkStringBytes)
            throw new InvalidOperationException($"Bulk string size {len} bytes exceeds maximum allowed size of {_maxBulkStringBytes} bytes. Possible DoS attack or misconfigured server.");

        // The payload is always fully overwritten by ReadExactAsync, so zero-init is unnecessary work here.
        var buf = poolBulk
            ? ArrayPool<byte>.Shared.Rent(len)
            : GC.AllocateUninitializedArray<byte>(len);
        await ReadExactAsync(buf.AsMemory(0, len), ct).ConfigureAwait(false);

        // consume CRLF
        var cr = await ReadByteAsync(ct).ConfigureAwait(false);
        var lf = await ReadByteAsync(ct).ConfigureAwait(false);
        if (cr != (byte)'\r' || lf != (byte)'\n')
            throw new InvalidOperationException("Invalid bulk string terminator.");

        return RedisRespReader.RespValue.BulkString(buf, len, poolBulk);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RedisRespReader.RespValue> ReadCountInternalAsync(RedisResponseMode responseMode, CancellationToken ct)
    {
        var prefix = await ReadByteAsync(ct).ConfigureAwait(false);
        return prefix switch
        {
            (byte)'-' => RedisRespReader.RespValue.Error(await ReadLineAsync(ct).ConfigureAwait(false)),
            (byte)':' => RedisRespReader.RespValue.Integer(await ReadInt64LineAsync(ct).ConfigureAwait(false)),
            (byte)'*' => await ReadCountAggregateAsync(responseMode, ct).ConfigureAwait(false),
            (byte)'~' => await ReadCountAggregateAsync(responseMode, ct).ConfigureAwait(false),
            (byte)'|' => await ReadCountAttributeWrappedAsync(responseMode, ct).ConfigureAwait(false),
            (byte)'>' => await ReadPushAsync(poolBulk: false, ct).ConfigureAwait(false),
            (byte)'+' => RedisRespReader.RespValue.SimpleString(await ReadSimpleStringAsync(ct).ConfigureAwait(false)),
            (byte)'$' => responseMode == RedisResponseMode.BulkStringDiscard
                ? await ReadDiscardedBulkStringAsync(ct).ConfigureAwait(false)
                : await ReadBulkStringAsync(poolBulk: false, ct).ConfigureAwait(false),
            (byte)'_' => await ReadNullAsync(ct).ConfigureAwait(false),
            (byte)',' => RedisRespReader.RespValue.SimpleString(await ReadLineAsync(ct).ConfigureAwait(false)),
            (byte)'#' => await ReadBooleanAsync(ct).ConfigureAwait(false),
            (byte)'(' => RedisRespReader.RespValue.SimpleString(await ReadLineAsync(ct).ConfigureAwait(false)),
            (byte)'=' => await ReadVerbatimStringAsync(poolBulk: false, ct).ConfigureAwait(false),
            (byte)'!' => await ReadBlobErrorAsync(ct).ConfigureAwait(false),
            (byte)'%' => await ReadMapAsArrayAsync(poolBulk: false, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported RESP type: {(char)prefix}")
        };
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RedisRespReader.RespValue> ReadCountAggregateAsync(RedisResponseMode responseMode, CancellationToken ct)
    {
        var len = await ReadInt32LineAsync(ct).ConfigureAwait(false);
        if (len == -1)
            return RedisRespReader.RespValue.Integer(0);

        if (len < 0)
            throw new InvalidOperationException($"Invalid aggregate length: {len}");

        var count = responseMode switch
        {
            RedisResponseMode.BulkStringArrayCountAllowNulls => await ReadBulkStringArrayCountAsync(len, allowNullBulkStrings: true, ct).ConfigureAwait(false),
            RedisResponseMode.ZRangeWithScoresCount => await ReadZRangeWithScoresCountAsync(len, ct).ConfigureAwait(false),
            RedisResponseMode.FtSearchCount => await ReadFtSearchCountAsync(len, ct).ConfigureAwait(false),
            RedisResponseMode.TimeSeriesRangeCount => await ReadTimeSeriesRangeCountAsync(len, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported count response mode: {responseMode}")
        };

        return RedisRespReader.RespValue.Integer(count);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RedisRespReader.RespValue> ReadCountAttributeWrappedAsync(RedisResponseMode responseMode, CancellationToken ct)
    {
        var pairCount = await ReadInt32LineAsync(ct).ConfigureAwait(false);
        if (pairCount >= 0)
            await SkipAggregateValuesAsync(checked(pairCount * 2), ct).ConfigureAwait(false);

        return await ReadCountInternalAsync(responseMode, ct).ConfigureAwait(false);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RedisRespReader.RespValue> ReadDiscardedBulkStringAsync(CancellationToken ct)
    {
        var len = await ReadInt32LineAsync(ct).ConfigureAwait(false);
        if (len == -1)
            return RedisRespReader.RespValue.NullBulkString();

        if (len < 0)
            throw new InvalidOperationException($"Invalid bulk length: {len}");

        ValidateBulkLength(len);
        await SkipBulkPayloadAsync(len, ct).ConfigureAwait(false);
        return RedisRespReader.RespValue.BulkString(Array.Empty<byte>(), 0, pooled: false);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> ReadBulkStringArrayCountAsync(int len, bool allowNullBulkStrings, CancellationToken ct)
    {
        await EnterAggregateDepthAsync(len, ct).ConfigureAwait(false);
        try
        {
            for (var i = 0; i < len; i++)
                await ReadBulkStringElementAsync(allowNullBulkStrings, ct).ConfigureAwait(false);

            return len;
        }
        finally
        {
            _currentArrayDepth--;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> ReadZRangeWithScoresCountAsync(int len, CancellationToken ct)
    {
        if ((len & 1) != 0)
            throw new InvalidOperationException("ZRANGE WITHSCORES response must contain an even number of elements.");

        await EnterAggregateDepthAsync(len, ct).ConfigureAwait(false);
        try
        {
            for (var i = 0; i < len; i += 2)
            {
                await ReadBulkStringElementAsync(allowNullBulkStrings: false, ct).ConfigureAwait(false);
                await ReadScoreElementAsync(ct).ConfigureAwait(false);
            }

            return len / 2;
        }
        finally
        {
            _currentArrayDepth--;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> ReadFtSearchCountAsync(int len, CancellationToken ct)
    {
        if (len == 0)
            return 0;

        await EnterAggregateDepthAsync(len, ct).ConfigureAwait(false);
        try
        {
            var total = await ReadIntegerLikeElementAsync(ct).ConfigureAwait(false);
            if (len > 1)
                await SkipAggregateValuesAsync(len - 1, ct).ConfigureAwait(false);

            return checked((int)total);
        }
        finally
        {
            _currentArrayDepth--;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> ReadTimeSeriesRangeCountAsync(int len, CancellationToken ct)
    {
        await EnterAggregateDepthAsync(len, ct).ConfigureAwait(false);
        try
        {
            for (var i = 0; i < len; i++)
                await ReadTimeSeriesRangeEntryAsync(ct).ConfigureAwait(false);

            return len;
        }
        finally
        {
            _currentArrayDepth--;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask ReadBulkStringElementAsync(bool allowNullBulkStrings, CancellationToken ct)
    {
        while (true)
        {
            var prefix = await ReadByteAsync(ct).ConfigureAwait(false);
            if (prefix == (byte)'|')
            {
                var pairCount = await ReadInt32LineAsync(ct).ConfigureAwait(false);
                if (pairCount >= 0)
                    await SkipAggregateValuesAsync(checked(pairCount * 2), ct).ConfigureAwait(false);
                continue;
            }

            if (prefix != (byte)'$')
                throw new InvalidOperationException($"Unexpected array element type: {(char)prefix}");

            var len = await ReadInt32LineAsync(ct).ConfigureAwait(false);
            if (len == -1)
            {
                if (allowNullBulkStrings)
                    return;

                throw new InvalidOperationException("Null bulk string was not expected in this response.");
            }

            if (len < 0)
                throw new InvalidOperationException($"Invalid bulk length: {len}");

            ValidateBulkLength(len);
            await SkipBulkPayloadAsync(len, ct).ConfigureAwait(false);
            return;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask ReadScoreElementAsync(CancellationToken ct)
    {
        while (true)
        {
            var prefix = await ReadByteAsync(ct).ConfigureAwait(false);
            if (prefix == (byte)'|')
            {
                var pairCount = await ReadInt32LineAsync(ct).ConfigureAwait(false);
                if (pairCount >= 0)
                    await SkipAggregateValuesAsync(checked(pairCount * 2), ct).ConfigureAwait(false);
                continue;
            }

            switch (prefix)
            {
                case (byte)'$':
                {
                    var len = await ReadInt32LineAsync(ct).ConfigureAwait(false);
                    if (len <= -1)
                        throw new InvalidOperationException("Score response cannot be null.");

                    ValidateBulkLength(len);
                    if (!await TryReadBulkDoubleAsync(len, ct).ConfigureAwait(false))
                        throw new InvalidOperationException("Invalid score value.");
                    return;
                }
                case (byte)'+':
                case (byte)',':
                case (byte)'(':
                {
                    var text = await ReadLineAsync(ct).ConfigureAwait(false);
                    if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                        throw new InvalidOperationException($"Invalid score value: {text}");
                    return;
                }
                default:
                    throw new InvalidOperationException($"Unexpected score type: {(char)prefix}");
            }
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<long> ReadIntegerLikeElementAsync(CancellationToken ct)
    {
        while (true)
        {
            var prefix = await ReadByteAsync(ct).ConfigureAwait(false);
            if (prefix == (byte)'|')
            {
                var pairCount = await ReadInt32LineAsync(ct).ConfigureAwait(false);
                if (pairCount >= 0)
                    await SkipAggregateValuesAsync(checked(pairCount * 2), ct).ConfigureAwait(false);
                continue;
            }

            switch (prefix)
            {
                case (byte)':':
                    return await ReadInt64LineAsync(ct).ConfigureAwait(false);
                case (byte)'+':
                case (byte)',':
                case (byte)'(':
                {
                    var text = await ReadLineAsync(ct).ConfigureAwait(false);
                    if (!long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
                        throw new InvalidOperationException($"Invalid integer value: {text}");
                    return value;
                }
                case (byte)'$':
                {
                    var len = await ReadInt32LineAsync(ct).ConfigureAwait(false);
                    if (len <= -1)
                        throw new InvalidOperationException("Integer response cannot be null.");

                    ValidateBulkLength(len);
                    var result = await TryReadBulkLongAsync(len, ct).ConfigureAwait(false);
                    if (!result.Success)
                        throw new InvalidOperationException("Invalid integer value.");
                    return result.Value;
                }
                default:
                    throw new InvalidOperationException($"Unexpected integer-like type: {(char)prefix}");
            }
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask ReadTimeSeriesRangeEntryAsync(CancellationToken ct)
    {
        while (true)
        {
            var prefix = await ReadByteAsync(ct).ConfigureAwait(false);
            if (prefix == (byte)'|')
            {
                var pairCount = await ReadInt32LineAsync(ct).ConfigureAwait(false);
                if (pairCount >= 0)
                    await SkipAggregateValuesAsync(checked(pairCount * 2), ct).ConfigureAwait(false);
                continue;
            }

            if (prefix is not ((byte)'*') and not ((byte)'~'))
                throw new InvalidOperationException($"Unexpected TS.RANGE entry type: {(char)prefix}");

            var len = await ReadInt32LineAsync(ct).ConfigureAwait(false);
            if (len != 2)
                throw new InvalidOperationException($"Unexpected TS.RANGE entry length: {len}");

            await EnterAggregateDepthAsync(len, ct).ConfigureAwait(false);
            try
            {
                await SkipRespValueAsync(ct).ConfigureAwait(false);
                await SkipRespValueAsync(ct).ConfigureAwait(false);
                return;
            }
            finally
            {
                _currentArrayDepth--;
            }
        }
    }

    private async ValueTask<bool> TryReadBulkDoubleAsync(int len, CancellationToken ct)
    {
        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(len);
            await ReadExactAsync(rented.AsMemory(0, len), ct).ConfigureAwait(false);

            var cr = await ReadByteAsync(ct).ConfigureAwait(false);
            var lf = await ReadByteAsync(ct).ConfigureAwait(false);
            if (cr != (byte)'\r' || lf != (byte)'\n')
                throw new InvalidOperationException("Invalid bulk string terminator.");

            return Utf8Parser.TryParse(rented.AsSpan(0, len), out double _, out var consumed) && consumed == len;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private async ValueTask<(bool Success, long Value)> TryReadBulkLongAsync(int len, CancellationToken ct)
    {
        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(len);
            await ReadExactAsync(rented.AsMemory(0, len), ct).ConfigureAwait(false);

            var cr = await ReadByteAsync(ct).ConfigureAwait(false);
            var lf = await ReadByteAsync(ct).ConfigureAwait(false);
            if (cr != (byte)'\r' || lf != (byte)'\n')
                throw new InvalidOperationException("Invalid bulk string terminator.");

            var parsed = Utf8Parser.TryParse(rented.AsSpan(0, len), out long parsedValue, out var consumed) && consumed == len;
            return (parsed, parsed ? parsedValue : 0L);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void ValidateBulkLength(int len)
    {
        if (_maxBulkStringBytes >= 0 && len > _maxBulkStringBytes)
            throw new InvalidOperationException($"Bulk string size {len} bytes exceeds maximum allowed size of {_maxBulkStringBytes} bytes. Possible DoS attack or misconfigured server.");
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask SkipBulkPayloadAsync(int len, CancellationToken ct)
    {
        await SkipBytesAsync(len, ct).ConfigureAwait(false);

        var cr = await ReadByteAsync(ct).ConfigureAwait(false);
        var lf = await ReadByteAsync(ct).ConfigureAwait(false);
        if (cr != (byte)'\r' || lf != (byte)'\n')
            throw new InvalidOperationException("Invalid bulk string terminator.");
    }

    private async ValueTask SkipBytesAsync(int len, CancellationToken ct)
    {
        var remaining = len;
        while (remaining > 0)
        {
            if (_pos >= _len)
                await FillAsync(ct).ConfigureAwait(false);

            var toSkip = Math.Min(remaining, _len - _pos);
            _pos += toSkip;
            remaining -= toSkip;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask SkipAggregateValuesAsync(int len, CancellationToken ct)
    {
        await EnterAggregateDepthAsync(len, ct).ConfigureAwait(false);
        try
        {
            for (var i = 0; i < len; i++)
                await SkipRespValueAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _currentArrayDepth--;
        }
    }

    private async ValueTask SkipRespValueAsync(CancellationToken ct)
    {
        var prefix = await ReadByteAsync(ct).ConfigureAwait(false);
        await SkipRespValueAsync(prefix, ct).ConfigureAwait(false);
    }

    private async ValueTask SkipRespValueAsync(byte prefix, CancellationToken ct)
    {
        switch (prefix)
        {
            case (byte)'+':
            case (byte)'-':
            case (byte)',':
            case (byte)'(':
            case (byte)'_':
            case (byte)'#':
                _ = await ReadLineAsync(ct).ConfigureAwait(false);
                return;
            case (byte)':':
                _ = await ReadInt64LineAsync(ct).ConfigureAwait(false);
                return;
            case (byte)'$':
            case (byte)'=':
            case (byte)'!':
            {
                var len = await ReadInt32LineAsync(ct).ConfigureAwait(false);
                if (len == -1)
                    return;

                if (len < 0)
                    throw new InvalidOperationException($"Invalid bulk length: {len}");

                ValidateBulkLength(len);
                await SkipBulkPayloadAsync(len, ct).ConfigureAwait(false);
                return;
            }
            case (byte)'*':
            case (byte)'~':
            case (byte)'>':
            {
                var len = await ReadInt32LineAsync(ct).ConfigureAwait(false);
                if (len == -1 && prefix is not (byte)'>')
                    return;

                if (len < 0)
                    throw new InvalidOperationException($"Invalid aggregate length: {len}");

                await SkipAggregateValuesAsync(len, ct).ConfigureAwait(false);
                return;
            }
            case (byte)'%':
            {
                var pairCount = await ReadInt32LineAsync(ct).ConfigureAwait(false);
                if (pairCount == -1)
                    return;

                if (pairCount < 0)
                    throw new InvalidOperationException($"Invalid map length: {pairCount}");

                await SkipAggregateValuesAsync(checked(pairCount * 2), ct).ConfigureAwait(false);
                return;
            }
            case (byte)'|':
            {
                var pairCount = await ReadInt32LineAsync(ct).ConfigureAwait(false);
                if (pairCount >= 0)
                    await SkipAggregateValuesAsync(checked(pairCount * 2), ct).ConfigureAwait(false);
                await SkipRespValueAsync(ct).ConfigureAwait(false);
                return;
            }
            default:
                throw new InvalidOperationException($"Unsupported RESP type: {(char)prefix}");
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask ReadExactAsync(Memory<byte> destination, CancellationToken ct)
    {
        var written = 0;
        while (written < destination.Length)
        {
            if (_pos >= _len)
                await FillAsync(ct).ConfigureAwait(false);

            var destSpan = destination.Span;
            var available = _len - _pos;
            var toCopy = Math.Min(available, destSpan.Length - written);
            CopyBufferToDestination(destSpan, written, toCopy, _pos);
            _pos += toCopy;
            written += toCopy;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void CopyBufferToDestination(Span<byte> destination, int destOffset, int count, int srcOffset)
    {
        if (count <= 0) return;

        if (_useUnsafeFastPath)
        {
            unsafe
            {
                fixed (byte* src = &_buffer[srcOffset])
                fixed (byte* dst = destination)
                {
                    Buffer.MemoryCopy(src, dst + destOffset, destination.Length - destOffset, count);
                }
            }
        }
        else
        {
            _buffer.AsSpan(srcOffset, count).CopyTo(destination.Slice(destOffset, count));
        }
    }

    private async ValueTask<RedisRespReader.RespValue> ReadArrayAsync(bool poolBulk, CancellationToken ct)
    {
        var len = await ReadInt32LineAsync(ct).ConfigureAwait(false);

        if (len == -1)
            return RedisRespReader.RespValue.NullArray();

        if (len < 0)
            throw new InvalidOperationException($"Invalid array length: {len}");

        return await ReadAggregateItemsAsync(poolBulk, len, isPush: false, ct).ConfigureAwait(false);
    }

    private async ValueTask<RedisRespReader.RespValue> ReadSetAsync(bool poolBulk, CancellationToken ct)
    {
        var len = await ReadInt32LineAsync(ct).ConfigureAwait(false);

        if (len == -1)
            return RedisRespReader.RespValue.NullArray();

        if (len < 0)
            throw new InvalidOperationException($"Invalid set length: {len}");

        return await ReadAggregateItemsAsync(poolBulk, len, isPush: false, ct).ConfigureAwait(false);
    }

    private async ValueTask<RedisRespReader.RespValue> ReadPushAsync(bool poolBulk, CancellationToken ct)
    {
        var len = await ReadInt32LineAsync(ct).ConfigureAwait(false);
        if (len < 0)
            throw new InvalidOperationException($"Invalid push length: {len}");

        return await ReadAggregateItemsAsync(poolBulk, len, isPush: true, ct).ConfigureAwait(false);
    }

    private async ValueTask<RedisRespReader.RespValue> ReadMapAsArrayAsync(bool poolBulk, CancellationToken ct)
    {
        var pairCount = await ReadInt32LineAsync(ct).ConfigureAwait(false);
        if (pairCount == -1)
            return RedisRespReader.RespValue.NullArray();
        if (pairCount < 0)
            throw new InvalidOperationException($"Invalid map length: {pairCount}");

        return await ReadAggregateItemsAsync(poolBulk, checked(pairCount * 2), isPush: false, ct).ConfigureAwait(false);
    }

    private async ValueTask<RedisRespReader.RespValue> ReadAttributeWrappedAsync(bool poolBulk, CancellationToken ct)
    {
        var pairCount = await ReadInt32LineAsync(ct).ConfigureAwait(false);
        if (pairCount < 0)
            return await ReadAsync(poolBulk, ct).ConfigureAwait(false);

        await EnterAggregateDepthAsync(pairCount * 2, ct).ConfigureAwait(false);
        try
        {
            for (var i = 0; i < pairCount * 2; i++)
            {
                var attr = await ReadAsync(poolBulk, ct).ConfigureAwait(false);
                RedisRespReader.ReturnBuffers(attr);
            }
        }
        finally
        {
            _currentArrayDepth--;
        }

        return await ReadAsync(poolBulk, ct).ConfigureAwait(false);
    }

    private async ValueTask<RedisRespReader.RespValue> ReadAggregateItemsAsync(bool poolBulk, int len, bool isPush, CancellationToken ct)
    {
        await EnterAggregateDepthAsync(len, ct).ConfigureAwait(false);
        try
        {
            var items = RedisRespReader.RentArray(len);
            var filled = 0;
            try
            {
                for (var i = 0; i < len; i++)
                {
                    items[i] = await ReadAsync(poolBulk, ct).ConfigureAwait(false);
                    filled++;
                }

                return isPush
                    ? RedisRespReader.RespValue.Push(items, len, pooled: true)
                    : RedisRespReader.RespValue.Array(items, len, pooled: true);
            }
            catch
            {
                for (var i = 0; i < filled; i++)
                    RedisRespReader.ReturnBuffers(items[i]);
                RedisRespReader.ReturnArray(items, len);
                throw;
            }
        }
        finally
        {
            _currentArrayDepth--;
        }
    }

    private ValueTask EnterAggregateDepthAsync(int len, CancellationToken ct)
    {
        _ = ct;
        _ = len;
        _currentArrayDepth++;
        if (_maxArrayDepth >= 0 && _currentArrayDepth > _maxArrayDepth)
        {
            _currentArrayDepth--;
            throw new InvalidOperationException($"Array nesting depth {_currentArrayDepth} exceeds maximum allowed depth of {_maxArrayDepth}. Possible stack overflow attack.");
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask<RedisRespReader.RespValue> ReadNullAsync(CancellationToken ct)
    {
        var line = await ReadLineAsync(ct).ConfigureAwait(false);
        if (line.Length != 0)
            throw new InvalidOperationException($"Invalid null response payload: {line}");
        return RedisRespReader.RespValue.NullBulkString();
    }

    private async ValueTask<RedisRespReader.RespValue> ReadBooleanAsync(CancellationToken ct)
    {
        var line = await ReadLineAsync(ct).ConfigureAwait(false);
        if (line.Length == 1 && (line[0] == 't' || line[0] == 'T'))
            return RedisRespReader.RespValue.Integer(1);
        if (line.Length == 1 && (line[0] == 'f' || line[0] == 'F'))
            return RedisRespReader.RespValue.Integer(0);
        throw new InvalidOperationException($"Invalid boolean response: {line}");
    }

    private async ValueTask<RedisRespReader.RespValue> ReadVerbatimStringAsync(bool poolBulk, CancellationToken ct)
    {
        var value = await ReadBulkStringAsync(poolBulk, ct).ConfigureAwait(false);
        if (value.Kind != RedisRespReader.RespKind.BulkString || value.Bulk is null)
            return value;

        if (value.BulkLength <= 4 || value.Bulk[3] != (byte)':')
            return value;

        var payloadLen = value.BulkLength - 4;
        var payload = GC.AllocateUninitializedArray<byte>(payloadLen);
        Buffer.BlockCopy(value.Bulk, 4, payload, 0, payloadLen);
        if (value.BulkIsPooled)
            ArrayPool<byte>.Shared.Return(value.Bulk);

        return RedisRespReader.RespValue.BulkString(payload, payloadLen, pooled: false);
    }

    private async ValueTask<RedisRespReader.RespValue> ReadBlobErrorAsync(CancellationToken ct)
    {
        var bulk = await ReadBulkStringAsync(poolBulk: false, ct).ConfigureAwait(false);
        return bulk.Kind switch
        {
            RedisRespReader.RespKind.NullBulkString => RedisRespReader.RespValue.Error(string.Empty),
            RedisRespReader.RespKind.BulkString when bulk.Bulk is not null => RedisRespReader.RespValue.Error(Encoding.UTF8.GetString(bulk.Bulk, 0, bulk.BulkLength)),
            _ => throw new InvalidOperationException("Invalid blob error payload.")
        };
    }

    /// <summary>
    /// Asynchronously releases resources used by the current instance.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return ValueTask.CompletedTask;
        if (Volatile.Read(ref _activeReaders) == 0)
            ReturnBufferIfNeeded();
        return ValueTask.CompletedTask;
    }

    private void CompleteRead()
    {
        if (Interlocked.Decrement(ref _activeReaders) == 0 && Volatile.Read(ref _disposed) == 1)
            ReturnBufferIfNeeded();
    }

    private void ReturnBufferIfNeeded()
    {
        if (Interlocked.Exchange(ref _bufferReturned, 1) == 1)
            return;

        var buffer = Interlocked.Exchange(ref _buffer, Array.Empty<byte>());
        if (buffer.Length != 0)
            ArrayPool<byte>.Shared.Return(buffer);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> ReadInt32LineAsync(CancellationToken ct)
    {
        var value = await ReadInt64LineAsync(ct).ConfigureAwait(false);
        if (value is < int.MinValue or > int.MaxValue)
            throw new InvalidOperationException($"Integer value out of range: {value}");
        return (int)value;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<long> ReadInt64LineAsync(CancellationToken ct)
    {
        var value = 0L;
        var negative = false;
        var sawDigit = false;
        var sawSign = false;

        while (true)
        {
            if (_pos >= _len)
                await FillAsync(ct).ConfigureAwait(false);

            var b = _buffer[_pos++];
            if (b == (byte)'\n')
                break;
            if (b == (byte)'\r')
                continue;

            if (b == (byte)'-' && !sawSign && !sawDigit)
            {
                negative = true;
                sawSign = true;
                continue;
            }

            if (b is >= (byte)'0' and <= (byte)'9')
            {
                sawDigit = true;
                value = checked(value * 10 + (b - (byte)'0'));
                continue;
            }

            throw new InvalidOperationException($"Invalid integer response byte: {(char)b}");
        }

        if (!sawDigit)
            throw new InvalidOperationException("Invalid integer response: empty");

        return negative ? -value : value;
    }
}
