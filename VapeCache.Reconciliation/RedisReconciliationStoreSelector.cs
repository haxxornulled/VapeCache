using Microsoft.Extensions.Options;

namespace VapeCache.Reconciliation;

internal sealed class RedisReconciliationStoreSelector : IRedisReconciliationStore
{
    private readonly IOptionsMonitor<RedisReconciliationStoreOptions> _options;
    private readonly SqliteReconciliationStore _sqlite;
    private readonly InMemoryReconciliationStore _memory;

    public RedisReconciliationStoreSelector(
        IOptionsMonitor<RedisReconciliationStoreOptions> options,
        SqliteReconciliationStore sqlite,
        InMemoryReconciliationStore memory)
    {
        _options = options;
        _sqlite = sqlite;
        _memory = memory;
    }

    private IRedisReconciliationStore Current => _options.CurrentValue.UseSqlite ? _sqlite : _memory;

    public ValueTask<int> CountAsync(CancellationToken ct) => Current.CountAsync(ct);

    public ValueTask<bool> TryUpsertWriteAsync(string key, ReadOnlyMemory<byte> value, DateTimeOffset trackedAt, DateTimeOffset? expiresAt, CancellationToken ct)
        => Current.TryUpsertWriteAsync(key, value, trackedAt, expiresAt, ct);

    public ValueTask<bool> TryUpsertDeleteAsync(string key, DateTimeOffset trackedAt, CancellationToken ct)
        => Current.TryUpsertDeleteAsync(key, trackedAt, ct);

    public ValueTask<IReadOnlyList<TrackedOperation>> SnapshotAsync(int maxOperations, CancellationToken ct)
        => Current.SnapshotAsync(maxOperations, ct);

    public ValueTask RemoveAsync(IReadOnlyList<string> keys, CancellationToken ct)
        => Current.RemoveAsync(keys, ct);

    public ValueTask ClearAsync(CancellationToken ct)
        => Current.ClearAsync(ct);
}
