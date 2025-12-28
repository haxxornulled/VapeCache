namespace VapeCache.Abstractions.Connections;

public interface IRedisCommandExecutor : IAsyncDisposable
{
    ValueTask<byte[]?> GetAsync(string key, CancellationToken ct);
    ValueTask<byte[]?> GetExAsync(string key, TimeSpan? ttl, CancellationToken ct);
    ValueTask<byte[]?[]> MGetAsync(string[] keys, CancellationToken ct);

    ValueTask<bool> SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct);
    ValueTask<bool> MSetAsync((string Key, ReadOnlyMemory<byte> Value)[] items, CancellationToken ct);

    ValueTask<bool> DeleteAsync(string key, CancellationToken ct);
    ValueTask<long> TtlSecondsAsync(string key, CancellationToken ct);
    ValueTask<long> PTtlMillisecondsAsync(string key, CancellationToken ct);
    ValueTask<long> UnlinkAsync(string key, CancellationToken ct);

    // Lease-based reads (avoid allocating byte[] on hot paths; caller must Dispose).
    ValueTask<RedisValueLease> GetLeaseAsync(string key, CancellationToken ct);
    ValueTask<RedisValueLease> GetExLeaseAsync(string key, TimeSpan? ttl, CancellationToken ct);

    // Hashes
    ValueTask<long> HSetAsync(string key, string field, ReadOnlyMemory<byte> value, CancellationToken ct);
    ValueTask<byte[]?> HGetAsync(string key, string field, CancellationToken ct);
    ValueTask<byte[]?[]> HMGetAsync(string key, string[] fields, CancellationToken ct);
    ValueTask<RedisValueLease> HGetLeaseAsync(string key, string field, CancellationToken ct);

    // Lists
    ValueTask<long> LPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct);
    ValueTask<long> RPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct);
    ValueTask<byte[]?> LPopAsync(string key, CancellationToken ct);
    ValueTask<byte[]?> RPopAsync(string key, CancellationToken ct);
    ValueTask<byte[]?[]> LRangeAsync(string key, long start, long stop, CancellationToken ct);
    ValueTask<long> LLenAsync(string key, CancellationToken ct);
    ValueTask<RedisValueLease> LPopLeaseAsync(string key, CancellationToken ct);

    // Sets
    ValueTask<long> SAddAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct);
    ValueTask<long> SRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct);
    ValueTask<bool> SIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct);
    ValueTask<byte[]?[]> SMembersAsync(string key, CancellationToken ct);
    ValueTask<long> SCardAsync(string key, CancellationToken ct);

    // Server commands
    ValueTask<string> PingAsync(CancellationToken ct);
    ValueTask<string[]> ModuleListAsync(CancellationToken ct);
}
