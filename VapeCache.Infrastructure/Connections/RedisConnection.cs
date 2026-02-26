// ========================= File: Vapecache.Infrastructure/Connections/RedisConnection.cs =========================
using System.Net.Sockets;
using LanguageExt;
using LanguageExt.Common;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisConnection(long id, Socket socket, Stream stream) : IRedisConnection
{
    private int _disposed;
    private readonly bool _useSocketIo = stream is NetworkStream;

    public long Id { get; } = id;
    public Socket Socket => socket;
    public Stream Stream => stream;

    /// <summary>
    /// Executes value.
    /// </summary>
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
                    var sent = await socket.SendAsync(buffer.Slice(total), SocketFlags.None, ct).ConfigureAwait(false);
                    if (sent <= 0) throw new IOException("Socket send returned 0.");
                    total += sent;
                    RedisTelemetry.BytesSent.Add(sent);
                }
            }
            else
            {
                await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
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
    public async ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) == 1)
            return new Result<int>(new ObjectDisposedException(nameof(RedisConnection)));

        try
        {
            var read = _useSocketIo
                ? await socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false)
                : await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
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

        try { await stream.DisposeAsync().ConfigureAwait(false); } catch { }
        try { socket.Dispose(); } catch { }
    }
}
