using System.Collections.Concurrent;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Reconciliation;

internal interface IRedisReconciliationStore
{
    ValueTask<int> CountAsync(CancellationToken ct);
    ValueTask<bool> TryUpsertWriteAsync(string key, ReadOnlyMemory<byte> value, DateTimeOffset trackedAt, DateTimeOffset? expiresAt, CancellationToken ct);
    ValueTask<bool> TryUpsertDeleteAsync(string key, DateTimeOffset trackedAt, CancellationToken ct);
    ValueTask<IReadOnlyList<TrackedOperation>> SnapshotAsync(int maxOperations, CancellationToken ct);
    ValueTask RemoveAsync(IReadOnlyList<string> keys, CancellationToken ct);
    ValueTask ClearAsync(CancellationToken ct);
}

internal sealed record TrackedOperation
{
    public required OperationType Type { get; init; }
    public required string Key { get; init; }
    public byte[]? Value { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public required DateTimeOffset TrackedAt { get; init; }
}

internal enum OperationType
{
    Write = 1,
    Delete = 2
}

internal sealed class InMemoryReconciliationStore : IRedisReconciliationStore
{
    private readonly ConcurrentDictionary<string, TrackedOperation> _pending = new();

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<int> CountAsync(CancellationToken ct) => ValueTask.FromResult(_pending.Count);

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public ValueTask<bool> TryUpsertWriteAsync(string key, ReadOnlyMemory<byte> value, DateTimeOffset trackedAt, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        var op = new TrackedOperation
        {
            Type = OperationType.Write,
            Key = key,
            Value = value.ToArray(),
            ExpiresAt = expiresAt,
            TrackedAt = trackedAt
        };

        _pending.AddOrUpdate(key, op, (_, __) => op);
        return ValueTask.FromResult(true);
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public ValueTask<bool> TryUpsertDeleteAsync(string key, DateTimeOffset trackedAt, CancellationToken ct)
    {
        var op = new TrackedOperation
        {
            Type = OperationType.Delete,
            Key = key,
            TrackedAt = trackedAt
        };

        _pending.AddOrUpdate(key, op, (_, __) => op);
        return ValueTask.FromResult(true);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<IReadOnlyList<TrackedOperation>> SnapshotAsync(int maxOperations, CancellationToken ct)
    {
        var snapshot = _pending.Values.ToArray();
        if (maxOperations > 0 && snapshot.Length > maxOperations)
        {
            Array.Sort(snapshot, static (a, b) => a.TrackedAt.CompareTo(b.TrackedAt));
            Array.Resize(ref snapshot, maxOperations);
        }
        return ValueTask.FromResult<IReadOnlyList<TrackedOperation>>(snapshot);
    }

    /// <summary>
    /// Removes value.
    /// </summary>
    public ValueTask RemoveAsync(IReadOnlyList<string> keys, CancellationToken ct)
    {
        foreach (var key in keys)
            _pending.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask ClearAsync(CancellationToken ct)
    {
        _pending.Clear();
        return ValueTask.CompletedTask;
    }
}
