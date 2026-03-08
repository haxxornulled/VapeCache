using LanguageExt.Common;

namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Defines the redis connection pool contract.
/// </summary>
public interface IRedisConnectionPool : IAsyncDisposable
{
    /// <summary>
    /// Executes rent async.
    /// </summary>
    ValueTask<Result<IRedisConnectionLease>> RentAsync(CancellationToken ct);
}
