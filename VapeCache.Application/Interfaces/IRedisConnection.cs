using System.Net.Sockets;
using LanguageExt;
using LanguageExt.Common;

namespace VapeCache.Application.Connections;

public interface IRedisConnection : IAsyncDisposable
{
    Socket Socket { get; }
    Stream Stream { get; }

    ValueTask<Result<Unit>> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct);
    ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);
}

public interface IRedisConnectionLease : IAsyncDisposable
{
    IRedisConnection Connection { get; }
}

