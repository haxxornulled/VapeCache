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

    public RedisRespReaderState(Stream stream, int bufferSize = 8192, bool useUnsafeFastPath = false, int maxBulkStringBytes = 16 * 1024 * 1024, int maxArrayDepth = 64)
    {
        _stream = stream;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(256, bufferSize));
        _useUnsafeFastPath = useUnsafeFastPath;
        _maxBulkStringBytes = maxBulkStringBytes;
        _maxArrayDepth = maxArrayDepth;
        _currentArrayDepth = 0;
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

        // Use zero-initialized arrays to prevent garbage data that can cause JSON deserialization errors
        var buf = poolBulk
            ? ArrayPool<byte>.Shared.Rent(len)
            : new byte[len];
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

        // CRITICAL: Prevent stack overflow from deeply nested arrays
        _currentArrayDepth++;
        if (_maxArrayDepth >= 0 && _currentArrayDepth > _maxArrayDepth)
        {
            _currentArrayDepth--;
            throw new InvalidOperationException($"Array nesting depth {_currentArrayDepth} exceeds maximum allowed depth of {_maxArrayDepth}. Possible stack overflow attack.");
        }

        try
        {
            var items = RedisRespReader.RentArray(len);
            for (var i = 0; i < len; i++)
                items[i] = await ReadAsync(poolBulk, ct).ConfigureAwait(false);

            return RedisRespReader.RespValue.Array(items, len, pooled: true);
        }
        finally
        {
            _currentArrayDepth--;
        }
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
