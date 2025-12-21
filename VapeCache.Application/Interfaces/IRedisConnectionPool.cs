using LanguageExt.Common;

namespace VapeCache.Application.Connections;

public interface IRedisConnectionPool : IAsyncDisposable
{
    ValueTask<Result<IRedisConnectionLease>> RentAsync(CancellationToken ct);
}

