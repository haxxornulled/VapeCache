using LanguageExt.Common;

namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Defines the redis connection factory contract.
/// </summary>
public interface IRedisConnectionFactory : IAsyncDisposable
{
    /// <summary>
    /// Executes create async.
    /// </summary>
    ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct);

    /// <summary>
    /// Executes create or throw async.
    /// </summary>
    async ValueTask<IRedisConnection> CreateOrThrowAsync(CancellationToken ct)
    {
        var created = await CreateAsync(ct).ConfigureAwait(false);
        return created.Match(static succ => succ, static ex => throw ex);
    }
}
