using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Threading.Tasks.Sources;
using System.Runtime.CompilerServices;
using System.Text;

namespace VapeCache.Infrastructure.Connections;

// RESP reader that consumes directly from a socket into a reusable buffer (no Stream).
internal sealed class RedisRespSocketReaderState : IAsyncDisposable
{
    private readonly Socket _socket;
    private byte[] _buffer;
    private byte[]? _lineScratch;
    private int _pos;
    private int _len;
    private int _disposed;
    private int _readCallDepth;
    private long _totalBytesRead;
    private readonly bool _useUnsafeFastPath;
    private readonly SocketIoAwaitableEventArgs _recvArgs = new();
    private readonly int _maxBulkStringBytes;
    private readonly int _maxArrayDepth;
    private int _currentArrayDepth;
    private readonly Action<int>? _onBytesRead;

    public RedisRespSocketReaderState(
        Socket socket,
        int bufferSize = 8192,
        bool useUnsafeFastPath = false,
        int maxBulkStringBytes = 16 * 1024 * 1024,
        int maxArrayDepth = 64,
        Action<int>? onBytesRead = null)
    {
        _socket = socket;
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
    public ValueTask<RedisRespReader.RespValue> ReadAsync(bool poolBulk, CancellationToken ct)
    {
        return ReadInternalAsync(poolBulk, ct);
    }

    public ValueTask<RedisRespReader.RespValue> ReadCountAsync(RedisResponseMode responseMode, CancellationToken ct)
    {
        return ReadCountInternalAsync(responseMode, ct);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RedisRespReader.RespValue> ReadInternalAsync(bool poolBulk, CancellationToken ct)
    {
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
            (byte)':' => RedisRespReader.RespValue.Integer(ReadInt64(await ReadLineAsync(ct).ConfigureAwait(false))),
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
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RedisRespReader.RespValue> ReadCountInternalAsync(RedisResponseMode responseMode, CancellationToken ct)
    {
        var depth = Interlocked.Increment(ref _readCallDepth);
        var isTopLevelRead = depth == 1;
        var parseStartBytes = isTopLevelRead ? GetConsumedByteCount() : 0L;
        var parseStartTicks = isTopLevelRead ? Stopwatch.GetTimestamp() : 0L;

        try
        {
            var value = await ReadCountFrameAsync(responseMode, ct).ConfigureAwait(false);

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
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RedisRespReader.RespValue> ReadCountFrameAsync(RedisResponseMode responseMode, CancellationToken ct)
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        _pos = 0;
        _len = 0;

        var args = _recvArgs;
        args.ResetForOperation();
        args.SetBuffer(_buffer.AsMemory());

        int read;
        if (_socket.ReceiveAsync(args))
        {
            args.RegisterCancellation(ct);
            read = await args.WaitAsync().ConfigureAwait(false);
        }
        else
        {
            read = args.CompleteInlineOrThrow();
        }

        if (read == 0) throw new EndOfStreamException();
        RedisTelemetry.BytesReceived.Add(read);
        _onBytesRead?.Invoke(read);
        _totalBytesRead += read;
        _len = read;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
                var s = System.Text.Encoding.UTF8.GetString(_buffer, _pos, lineLen);
                _pos += lf + 1;
                return s;
            }

            // Line spans multiple fills: accumulate remaining bytes then continue.
            byte[]? rented = null;
            try
            {
                rented = _lineScratch;
                if (rented is null || rented.Length < 512)
                {
                    rented = ArrayPool<byte>.Shared.Rent(512);
                    ReturnLineScratchIfNeeded();
                    _lineScratch = rented;
                }

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
                            _lineScratch = bigger;
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

                    var end = total;
                    if (end >= 2 && rented[end - 2] == (byte)'\r' && rented[end - 1] == (byte)'\n')
                        end -= 2;
                    else if (end >= 1 && rented[end - 1] == (byte)'\n')
                        end -= 1;

                    return System.Text.Encoding.UTF8.GetString(rented, 0, end);
                }
            }
            finally
            {
                if (rented is not null && !ReferenceEquals(rented, _lineScratch))
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static long ReadInt64(string line)
    {
        if (!long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException($"Invalid integer response: {line}");
        return value;
    }

    private async ValueTask<RedisRespReader.RespValue> ReadBulkStringAsync(bool poolBulk, CancellationToken ct)
    {
        var header = await ReadLineAsync(ct).ConfigureAwait(false);
        if (!int.TryParse(header, out var len))
            throw new InvalidOperationException($"Invalid bulk length: {header}");

        if (len == -1)
            return RedisRespReader.RespValue.NullBulkString();

        if (len < 0)
            throw new InvalidOperationException($"Invalid bulk length: {header}");

        // CRITICAL: Prevent DoS attacks from malicious Redis server sending huge bulk strings
        if (_maxBulkStringBytes >= 0 && len > _maxBulkStringBytes)
            throw new InvalidOperationException($"Bulk string size {len} bytes exceeds maximum allowed size of {_maxBulkStringBytes} bytes. Possible DoS attack or misconfigured server.");

        // The payload is always fully overwritten by ReadExactAsync, so zero-init is unnecessary work here.
        var buf = poolBulk
            ? ArrayPool<byte>.Shared.Rent(len)
            : GC.AllocateUninitializedArray<byte>(len);
        await ReadExactAsync(buf.AsMemory(0, len), ct).ConfigureAwait(false);

        var cr = await ReadByteAsync(ct).ConfigureAwait(false);
        var lf = await ReadByteAsync(ct).ConfigureAwait(false);
        if (cr != (byte)'\r' || lf != (byte)'\n')
            throw new InvalidOperationException("Invalid bulk string terminator.");

        return RedisRespReader.RespValue.BulkString(buf, len, poolBulk);
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

        return await ReadCountFrameAsync(responseMode, ct).ConfigureAwait(false);
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
        await EnterAggregateDepthAsync().ConfigureAwait(false);
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

        await EnterAggregateDepthAsync().ConfigureAwait(false);
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

        await EnterAggregateDepthAsync().ConfigureAwait(false);
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
        await EnterAggregateDepthAsync().ConfigureAwait(false);
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
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
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
                    if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
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

            await EnterAggregateDepthAsync().ConfigureAwait(false);
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

    private ValueTask<bool> TryReadBulkDoubleAsync(int len, CancellationToken ct)
    {
        if (TryConsumeBufferedBulkPayload(len, out var payloadOffset))
        {
            var parsed = Utf8Parser.TryParse(_buffer.AsSpan(payloadOffset, len), out double _, out var consumed) && consumed == len;
            return ValueTask.FromResult(parsed);
        }

        return TryReadBulkDoubleSlowAsync(len, ct);
    }

    private async ValueTask<bool> TryReadBulkDoubleSlowAsync(int len, CancellationToken ct)
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

    private ValueTask<(bool Success, long Value)> TryReadBulkLongAsync(int len, CancellationToken ct)
    {
        if (TryConsumeBufferedBulkPayload(len, out var payloadOffset))
        {
            var parsed = Utf8Parser.TryParse(_buffer.AsSpan(payloadOffset, len), out long parsedValue, out var consumed) && consumed == len;
            return ValueTask.FromResult((parsed, parsed ? parsedValue : 0L));
        }

        return TryReadBulkLongSlowAsync(len, ct);
    }

    private async ValueTask<(bool Success, long Value)> TryReadBulkLongSlowAsync(int len, CancellationToken ct)
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryConsumeBufferedBulkPayload(int len, out int payloadOffset)
    {
        payloadOffset = _pos;
        if (_len - _pos < len + 2)
            return false;

        var terminatorOffset = _pos + len;
        if (_buffer[terminatorOffset] != (byte)'\r' || _buffer[terminatorOffset + 1] != (byte)'\n')
            throw new InvalidOperationException("Invalid bulk string terminator.");

        _pos = terminatorOffset + 2;
        return true;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private ValueTask SkipBulkPayloadAsync(int len, CancellationToken ct)
    {
        if (TryConsumeBufferedBulkPayload(len, out _))
            return ValueTask.CompletedTask;

        return SkipBulkPayloadSlowAsync(len, ct);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask SkipBulkPayloadSlowAsync(int len, CancellationToken ct)
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
        await EnterAggregateDepthAsync().ConfigureAwait(false);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        var header = await ReadLineAsync(ct).ConfigureAwait(false);
        if (!int.TryParse(header, out var len))
            throw new InvalidOperationException($"Invalid array length: {header}");

        if (len == -1)
            return RedisRespReader.RespValue.NullArray();

        if (len < 0)
            throw new InvalidOperationException($"Invalid array length: {header}");

        return await ReadAggregateItemsAsync(poolBulk, len, isPush: false, ct).ConfigureAwait(false);
    }

    private async ValueTask<RedisRespReader.RespValue> ReadSetAsync(bool poolBulk, CancellationToken ct)
    {
        var header = await ReadLineAsync(ct).ConfigureAwait(false);
        if (!int.TryParse(header, out var len))
            throw new InvalidOperationException($"Invalid set length: {header}");

        if (len == -1)
            return RedisRespReader.RespValue.NullArray();
        if (len < 0)
            throw new InvalidOperationException($"Invalid set length: {header}");

        return await ReadAggregateItemsAsync(poolBulk, len, isPush: false, ct).ConfigureAwait(false);
    }

    private async ValueTask<RedisRespReader.RespValue> ReadPushAsync(bool poolBulk, CancellationToken ct)
    {
        var header = await ReadLineAsync(ct).ConfigureAwait(false);
        if (!int.TryParse(header, out var len))
            throw new InvalidOperationException($"Invalid push length: {header}");
        if (len < 0)
            throw new InvalidOperationException($"Invalid push length: {header}");

        return await ReadAggregateItemsAsync(poolBulk, len, isPush: true, ct).ConfigureAwait(false);
    }

    private async ValueTask<RedisRespReader.RespValue> ReadMapAsArrayAsync(bool poolBulk, CancellationToken ct)
    {
        var header = await ReadLineAsync(ct).ConfigureAwait(false);
        if (!int.TryParse(header, out var pairCount))
            throw new InvalidOperationException($"Invalid map length: {header}");

        if (pairCount == -1)
            return RedisRespReader.RespValue.NullArray();
        if (pairCount < 0)
            throw new InvalidOperationException($"Invalid map length: {header}");

        return await ReadAggregateItemsAsync(poolBulk, checked(pairCount * 2), isPush: false, ct).ConfigureAwait(false);
    }

    private async ValueTask<RedisRespReader.RespValue> ReadAttributeWrappedAsync(bool poolBulk, CancellationToken ct)
    {
        var header = await ReadLineAsync(ct).ConfigureAwait(false);
        if (!int.TryParse(header, out var pairCount))
            throw new InvalidOperationException($"Invalid attribute length: {header}");

        if (pairCount < 0)
            return await ReadInternalAsync(poolBulk, ct).ConfigureAwait(false);

        await EnterAggregateDepthAsync().ConfigureAwait(false);
        try
        {
            for (var i = 0; i < pairCount * 2; i++)
            {
                var attr = await ReadInternalAsync(poolBulk, ct).ConfigureAwait(false);
                RedisRespReader.ReturnBuffers(attr);
            }
        }
        finally
        {
            _currentArrayDepth--;
        }

        return await ReadInternalAsync(poolBulk, ct).ConfigureAwait(false);
    }

    private async ValueTask<RedisRespReader.RespValue> ReadAggregateItemsAsync(bool poolBulk, int len, bool isPush, CancellationToken ct)
    {
        await EnterAggregateDepthAsync().ConfigureAwait(false);
        try
        {
            var items = RedisRespReader.RentArray(len);
            var filled = 0;
            try
            {
                for (var i = 0; i < len; i++)
                {
                    items[i] = await ReadInternalAsync(poolBulk, ct).ConfigureAwait(false);
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

    private ValueTask EnterAggregateDepthAsync()
    {
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

    /// <summary>
    /// Asynchronously releases resources used by the current instance.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return ValueTask.CompletedTask;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = Array.Empty<byte>();
        ReturnLineScratchIfNeeded();
        return ValueTask.CompletedTask;
    }

    private void ReturnLineScratchIfNeeded()
    {
        var lineScratch = Interlocked.Exchange(ref _lineScratch, null);
        if (lineScratch is not null && lineScratch.Length != 0)
            ArrayPool<byte>.Shared.Return(lineScratch);
    }
}
    // Minimal awaitable wrapper for SocketAsyncEventArgs to avoid per-receive allocations.
