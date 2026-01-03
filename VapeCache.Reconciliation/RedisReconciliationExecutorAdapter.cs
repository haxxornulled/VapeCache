using VapeCache.Infrastructure.Connections;

namespace VapeCache.Reconciliation;

internal interface IRedisReconciliationExecutor
{
    ValueTask<bool> SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct);
    ValueTask<bool> DeleteAsync(string key, CancellationToken ct);
}

internal sealed class RedisReconciliationExecutorAdapter : IRedisReconciliationExecutor
{
    private readonly RedisCommandExecutor _redis;

    public RedisReconciliationExecutorAdapter(RedisCommandExecutor redis)
    {
        _redis = redis;
    }

    public ValueTask<bool> SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct)
        => _redis.SetAsync(key, value, ttl, ct);

    public ValueTask<bool> DeleteAsync(string key, CancellationToken ct)
        => _redis.DeleteAsync(key, ct);
}
