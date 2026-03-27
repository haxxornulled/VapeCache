using System.Net.Sockets;
using System.Runtime.CompilerServices;
using LanguageExt;
using LanguageExt.Common;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisConnection : IRedisConnection
{
    private readonly Socket _socket;
    private readonly Stream _stream;
    private int _disposed;
    private readonly bool _useSocketIo;

    public RedisConnection(long id, Socket socket, Stream stream)
    {
        Id = id;
        _socket = socket;
        _stream = stream;
        _useSocketIo = stream is NetworkStream;
    }

    public long Id { get; }
    public Socket Socket => _socket;
    public Stream Stream => _stream;

    /// <summary>
    /// Executes value.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<Result<Unit>> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) == 1)
            return new Result<Unit>(new ObjectDisposedException(nameof(RedisConnection)));

        try
        {
            if (_useSocketIo)
            {
                var total = 0;
                while (total < buffer.Length)
                {
                    var sent = await _socket.SendAsync(buffer.Slice(total), SocketFlags.None, ct).ConfigureAwait(false);
                    if (sent <= 0) throw new IOException("Socket send returned 0.");
                    total += sent;
                    RedisTelemetry.BytesSent.Add(sent);
                }
            }
            else
            {
                await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
                RedisTelemetry.BytesSent.Add(buffer.Length);
            }
            return Prelude.unit;
        }
        catch (Exception ex)
        {
            return new Result<Unit>(ex);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) == 1)
            return new Result<int>(new ObjectDisposedException(nameof(RedisConnection)));

        try
        {
            var read = _useSocketIo
                ? await _socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false)
                : await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read > 0) RedisTelemetry.BytesReceived.Add(read);
            return read;
        }
        catch (Exception ex)
        {
            return new Result<int>(ex);
        }
    }

    /// <summary>
    /// Asynchronously releases resources used by the current instance.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { }
        try { _socket.Dispose(); } catch { }
    }
}
