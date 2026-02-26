using System.Buffers;
using System.Globalization;
using System.Net.Sockets;
using System.Threading.Tasks.Sources;
using System.Runtime.CompilerServices;

namespace VapeCache.Infrastructure.Connections;

// RESP reader that consumes directly from a socket into a reusable buffer (no Stream).
internal sealed class RedisRespSocketReaderState : IAsyncDisposable
{
    private readonly Socket _socket;
    private byte[] _buffer;
    private int _pos;
    private int _len;
    private int _disposed;
    private readonly bool _useUnsafeFastPath;
    private readonly SocketIoAwaitableEventArgs _recvArgs = new();
    private readonly int _maxBulkStringBytes;
    private readonly int _maxArrayDepth;
    private int _currentArrayDepth;

    public RedisRespSocketReaderState(Socket socket, int bufferSize = 8192, bool useUnsafeFastPath = false, int maxBulkStringBytes = 16 * 1024 * 1024, int maxArrayDepth = 64)
    {
        _socket = socket;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(256, bufferSize));
        _useUnsafeFastPath = useUnsafeFastPath;
        _maxBulkStringBytes = maxBulkStringBytes;
        _maxArrayDepth = maxArrayDepth;
        _currentArrayDepth = 0;
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
        var prefix = await ReadByteAsync(ct).ConfigureAwait(false);
        return prefix switch
        {
            (byte)'+' => RedisRespReader.RespValue.SimpleString(await ReadLineAsync(ct).ConfigureAwait(false)),
            (byte)'-' => RedisRespReader.RespValue.Error(await ReadLineAsync(ct).ConfigureAwait(false)),
            (byte)':' => RedisRespReader.RespValue.Integer(ReadInt64(await ReadLineAsync(ct).ConfigureAwait(false))),
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
        if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(RedisRespSocketReaderState));

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

        // Use zero-initialized arrays to prevent garbage data that can cause JSON deserialization errors
        var buf = poolBulk
            ? ArrayPool<byte>.Shared.Rent(len)
            : new byte[len];
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
                items[i] = await ReadInternalAsync(poolBulk, ct).ConfigureAwait(false);

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
}
    // Minimal awaitable wrapper for SocketAsyncEventArgs to avoid per-receive allocations.
