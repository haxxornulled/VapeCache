using LanguageExt.Common;

namespace VapeCache.Abstractions.Connections;

public interface IRedisConnectionFactory : IAsyncDisposable
{
    ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct);

    async ValueTask<IRedisConnection> CreateOrThrowAsync(CancellationToken ct)
    {
        var created = await CreateAsync(ct).ConfigureAwait(false);
        return created.Match(static succ => succ, static ex => throw ex);
    }
}
