using System.Net.Sockets;
using LanguageExt;
using LanguageExt.Common;

namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Defines the redis connection contract.
/// </summary>
public interface IRedisConnection : IAsyncDisposable
{
    /// <summary>
    /// Gets the socket.
    /// </summary>
    Socket Socket { get; }
    /// <summary>
    /// Gets the stream.
    /// </summary>
    Stream Stream { get; }

    /// <summary>
    /// Executes send async.
    /// </summary>
    ValueTask<Result<Unit>> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct);
    /// <summary>
    /// Executes receive async.
    /// </summary>
    ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);
}

/// <summary>
/// Defines the redis connection lease contract.
/// </summary>
public interface IRedisConnectionLease : IAsyncDisposable
{
    /// <summary>
    /// Gets the connection.
    /// </summary>
    IRedisConnection Connection { get; }
}
