using System.Buffers;
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

    private async ValueTask<byte> ReadByteAsync(CancellationToken ct)
    {
        if (_pos >= _len)
            await FillAsync(ct).ConfigureAwait(false);
        return _buffer[_pos++];
    }

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
                if (rented is not null)
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

    /// <summary>
    /// Asynchronously releases resources used by the current instance.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return ValueTask.CompletedTask;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = Array.Empty<byte>();
        return ValueTask.CompletedTask;
    }
}
    // Minimal awaitable wrapper for SocketAsyncEventArgs to avoid per-receive allocations.
