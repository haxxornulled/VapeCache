namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Defines the redis command executor contract.
/// </summary>
public interface IRedisCommandExecutor : IAsyncDisposable
{
    /// <summary>Create a client-side batch for pipelined operations.</summary>
    IRedisBatch CreateBatch();

    /// <summary>
    /// Executes get async.
    /// </summary>
    ValueTask<byte[]?> GetAsync(string key, CancellationToken ct);
    /// <summary>
    /// Executes get ex async.
    /// </summary>
    ValueTask<byte[]?> GetExAsync(string key, TimeSpan? ttl, CancellationToken ct);
    /// <summary>
    /// Executes mget async.
    /// </summary>
    ValueTask<byte[]?[]> MGetAsync(string[] keys, CancellationToken ct);
    /// <summary>
    /// Executes try get async.
    /// </summary>
    bool TryGetAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task);
    /// <summary>
    /// Executes try get ex async.
    /// </summary>
    bool TryGetExAsync(string key, TimeSpan? ttl, CancellationToken ct, out ValueTask<byte[]?> task);

    /// <summary>
    /// Executes set async.
    /// </summary>
    ValueTask<bool> SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct);
    /// <summary>
    /// Executes mset async.
    /// </summary>
    ValueTask<bool> MSetAsync((string Key, ReadOnlyMemory<byte> Value)[] items, CancellationToken ct);
    /// <summary>
    /// Executes try set async.
    /// </summary>
    bool TrySetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct, out ValueTask<bool> task);

    /// <summary>
    /// Executes delete async.
    /// </summary>
    ValueTask<bool> DeleteAsync(string key, CancellationToken ct);
    /// <summary>
    /// Executes ttl seconds async.
    /// </summary>
    ValueTask<long> TtlSecondsAsync(string key, CancellationToken ct);
    /// <summary>
    /// Executes pttl milliseconds async.
    /// </summary>
    ValueTask<long> PTtlMillisecondsAsync(string key, CancellationToken ct);
    /// <summary>
    /// Executes unlink async.
    /// </summary>
    ValueTask<long> UnlinkAsync(string key, CancellationToken ct);

    // Lease-based reads (avoid allocating byte[] on hot paths; caller must Dispose).
    /// <summary>
    /// Executes get lease async.
    /// </summary>
    ValueTask<RedisValueLease> GetLeaseAsync(string key, CancellationToken ct);
    /// <summary>
    /// Executes get ex lease async.
    /// </summary>
    ValueTask<RedisValueLease> GetExLeaseAsync(string key, TimeSpan? ttl, CancellationToken ct);
    /// <summary>
    /// Executes try get lease async.
    /// </summary>
    bool TryGetLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task);
    /// <summary>
    /// Executes try get ex lease async.
    /// </summary>
    bool TryGetExLeaseAsync(string key, TimeSpan? ttl, CancellationToken ct, out ValueTask<RedisValueLease> task);

    // Hashes
    /// <summary>
    /// Executes hset async.
    /// </summary>
    ValueTask<long> HSetAsync(string key, string field, ReadOnlyMemory<byte> value, CancellationToken ct);
    /// <summary>
    /// Executes hget async.
    /// </summary>
    ValueTask<byte[]?> HGetAsync(string key, string field, CancellationToken ct);
    /// <summary>
    /// Executes hmget async.
    /// </summary>
    ValueTask<byte[]?[]> HMGetAsync(string key, string[] fields, CancellationToken ct);
    /// <summary>
    /// Executes hget lease async.
    /// </summary>
    ValueTask<RedisValueLease> HGetLeaseAsync(string key, string field, CancellationToken ct);
    /// <summary>
    /// Executes try hget async.
    /// </summary>
    bool TryHGetAsync(string key, string field, CancellationToken ct, out ValueTask<byte[]?> task);

    // Lists
    /// <summary>
    /// Executes lpush async.
    /// </summary>
    ValueTask<long> LPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct);
    /// <summary>
    /// Executes rpush async.
    /// </summary>
    ValueTask<long> RPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct);
    /// <summary>
    /// Executes rpush many async.
    /// </summary>
    ValueTask<long> RPushManyAsync(string key, ReadOnlyMemory<byte>[] values, int count, CancellationToken ct);
    /// <summary>
    /// Executes lpop async.
    /// </summary>
    ValueTask<byte[]?> LPopAsync(string key, CancellationToken ct);
    /// <summary>
    /// Executes rpop async.
    /// </summary>
    ValueTask<byte[]?> RPopAsync(string key, CancellationToken ct);
    /// <summary>
    /// Executes try lpop async.
    /// </summary>
    bool TryLPopAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task);
    /// <summary>
    /// Executes try rpop async.
    /// </summary>
    bool TryRPopAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task);
    /// <summary>
    /// Executes lrange async.
    /// </summary>
    ValueTask<byte[]?[]> LRangeAsync(string key, long start, long stop, CancellationToken ct);
    /// <summary>
    /// Executes llen async.
    /// </summary>
    ValueTask<long> LLenAsync(string key, CancellationToken ct);
    /// <summary>
    /// Executes lpop lease async.
    /// </summary>
    ValueTask<RedisValueLease> LPopLeaseAsync(string key, CancellationToken ct);
    /// <summary>
    /// Executes rpop lease async.
    /// </summary>
    ValueTask<RedisValueLease> RPopLeaseAsync(string key, CancellationToken ct);
    /// <summary>
    /// Executes try lpop lease async.
    /// </summary>
    bool TryLPopLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task);
    /// <summary>
    /// Executes try rpop lease async.
    /// </summary>
    bool TryRPopLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task);

    // Sets
    /// <summary>
    /// Executes sadd async.
    /// </summary>
    ValueTask<long> SAddAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct);
    /// <summary>
    /// Executes srem async.
    /// </summary>
    ValueTask<long> SRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct);
    /// <summary>
    /// Executes sis member async.
    /// </summary>
    ValueTask<bool> SIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct);
    /// <summary>
    /// Executes smembers async.
    /// </summary>
    ValueTask<byte[]?[]> SMembersAsync(string key, CancellationToken ct);
    /// <summary>
    /// Executes scard async.
    /// </summary>
    ValueTask<long> SCardAsync(string key, CancellationToken ct);
    /// <summary>
    /// Executes try sis member async.
    /// </summary>
    bool TrySIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct, out ValueTask<bool> task);

    // Sorted Sets
    /// <summary>
    /// Executes zadd async.
    /// </summary>
    ValueTask<long> ZAddAsync(string key, double score, ReadOnlyMemory<byte> member, CancellationToken ct);
    /// <summary>
    /// Executes zrem async.
    /// </summary>
    ValueTask<long> ZRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct);
    /// <summary>
    /// Executes zcard async.
    /// </summary>
    ValueTask<long> ZCardAsync(string key, CancellationToken ct);
    /// <summary>
    /// Executes zscore async.
    /// </summary>
    ValueTask<double?> ZScoreAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct);
    /// <summary>
    /// Executes zrank async.
    /// </summary>
    ValueTask<long?> ZRankAsync(string key, ReadOnlyMemory<byte> member, bool descending, CancellationToken ct);
    /// <summary>
    /// Executes zincr by async.
    /// </summary>
    ValueTask<double> ZIncrByAsync(string key, double increment, ReadOnlyMemory<byte> member, CancellationToken ct);
    /// <summary>
    /// Executes zrange with scores async.
    /// </summary>
    ValueTask<(byte[] Member, double Score)[]> ZRangeWithScoresAsync(string key, long start, long stop, bool descending, CancellationToken ct);
    /// <summary>
    /// Executes zrange by score with scores async.
    /// </summary>
    ValueTask<(byte[] Member, double Score)[]> ZRangeByScoreWithScoresAsync(
        string key,
        double min,
        double max,
        bool descending,
        long? offset,
        long? count,
        CancellationToken ct);

    // JSON (RedisJSON module)
    /// <summary>
    /// Executes json get async.
    /// </summary>
    ValueTask<byte[]?> JsonGetAsync(string key, string? path, CancellationToken ct);
    // Lease-based JSON GET (avoid allocating byte[] on hot paths; caller must Dispose).
    /// <summary>
    /// Executes json get lease async.
    /// </summary>
    ValueTask<RedisValueLease> JsonGetLeaseAsync(string key, string? path, CancellationToken ct);
    /// <summary>
    /// Executes try json get lease async.
    /// </summary>
    bool TryJsonGetLeaseAsync(string key, string? path, CancellationToken ct, out ValueTask<RedisValueLease> task);
    /// <summary>
    /// Executes json set async.
    /// </summary>
    ValueTask<bool> JsonSetAsync(string key, string? path, ReadOnlyMemory<byte> json, CancellationToken ct);
    /// <summary>
    /// Executes json set lease async.
    /// </summary>
    ValueTask<bool> JsonSetLeaseAsync(string key, string? path, RedisValueLease json, CancellationToken ct);
    /// <summary>
    /// Executes json del async.
    /// </summary>
    ValueTask<long> JsonDelAsync(string key, string? path, CancellationToken ct);

    // RediSearch (FT.*)
    /// <summary>
    /// Executes ft create async.
    /// </summary>
    ValueTask<bool> FtCreateAsync(string index, string prefix, string[] fields, CancellationToken ct);
    /// <summary>
    /// Executes ft search async.
    /// </summary>
    ValueTask<string[]> FtSearchAsync(string index, string query, int? offset, int? count, CancellationToken ct);

    // RedisBloom (BF.*)
    /// <summary>
    /// Executes bf add async.
    /// </summary>
    ValueTask<bool> BfAddAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct);
    /// <summary>
    /// Executes bf exists async.
    /// </summary>
    ValueTask<bool> BfExistsAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct);

    // RedisTimeSeries (TS.*)
    /// <summary>
    /// Executes ts create async.
    /// </summary>
    ValueTask<bool> TsCreateAsync(string key, CancellationToken ct);
    /// <summary>
    /// Executes ts add async.
    /// </summary>
    ValueTask<long> TsAddAsync(string key, long timestamp, double value, CancellationToken ct);
    /// <summary>
    /// Executes ts range async.
    /// </summary>
    ValueTask<(long Timestamp, double Value)[]> TsRangeAsync(string key, long from, long to, CancellationToken ct);

    // Scan/streaming
    /// <summary>
    /// Executes scan async.
    /// </summary>
    IAsyncEnumerable<string> ScanAsync(string? pattern = null, int pageSize = 128, CancellationToken ct = default);
    /// <summary>
    /// Executes sscan async.
    /// </summary>
    IAsyncEnumerable<byte[]> SScanAsync(string key, string? pattern = null, int pageSize = 128, CancellationToken ct = default);
    /// <summary>
    /// Executes hscan async.
    /// </summary>
    IAsyncEnumerable<(string Field, byte[] Value)> HScanAsync(string key, string? pattern = null, int pageSize = 128, CancellationToken ct = default);
    /// <summary>
    /// Executes zscan async.
    /// </summary>
    IAsyncEnumerable<(byte[] Member, double Score)> ZScanAsync(string key, string? pattern = null, int pageSize = 128, CancellationToken ct = default);

    // Server commands
    /// <summary>
    /// Executes ping async.
    /// </summary>
    ValueTask<string> PingAsync(CancellationToken ct);
    /// <summary>
    /// Executes module list async.
    /// </summary>
    ValueTask<string[]> ModuleListAsync(CancellationToken ct);
}
