using LanguageExt.Common;

namespace VapeCache.Abstractions.Connections;

public interface IRedisConnectionPool : IAsyncDisposable
{
    ValueTask<Result<IRedisConnectionLease>> RentAsync(CancellationToken ct);
}
