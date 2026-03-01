using System.Buffers;
using System.IO;
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
    public async ValueTask<RedisRespReader.RespValue> ReadAsync(bool poolBulk, CancellationToken ct)
    {
        var prefix = await ReadByteAsync(ct).ConfigureAwait(false);
        return prefix switch
        {
            (byte)'+' => RedisRespReader.RespValue.SimpleString(await ReadLineAsync(ct).ConfigureAwait(false)),
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
    }

    private async ValueTask<byte> ReadByteAsync(CancellationToken ct)
    {
        if (_pos >= _len)
            await FillAsync(ct).ConfigureAwait(false);
        return _buffer[_pos++];
    }

    private async ValueTask FillAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(RedisRespReaderState));

        _pos = 0;
        _len = 0;
        var read = await _stream.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
        if (read == 0) throw new EndOfStreamException();
        RedisTelemetry.BytesReceived.Add(read);
        _onBytesRead?.Invoke(read);
        _len = read;
    }

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
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = Array.Empty<byte>();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<int> ReadInt32LineAsync(CancellationToken ct)
    {
        var value = await ReadInt64LineAsync(ct).ConfigureAwait(false);
        if (value is < int.MinValue or > int.MaxValue)
            throw new InvalidOperationException($"Integer value out of range: {value}");
        return (int)value;
    }

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
