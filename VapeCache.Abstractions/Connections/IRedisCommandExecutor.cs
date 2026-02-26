namespace VapeCache.Abstractions.Connections;

public interface IRedisCommandExecutor : IAsyncDisposable
{
    /// <summary>Create a client-side batch for pipelined operations.</summary>
    IRedisBatch CreateBatch();

    ValueTask<byte[]?> GetAsync(string key, CancellationToken ct);
    ValueTask<byte[]?> GetExAsync(string key, TimeSpan? ttl, CancellationToken ct);
    ValueTask<byte[]?[]> MGetAsync(string[] keys, CancellationToken ct);
    bool TryGetAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task);
    bool TryGetExAsync(string key, TimeSpan? ttl, CancellationToken ct, out ValueTask<byte[]?> task);

    ValueTask<bool> SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct);
    ValueTask<bool> MSetAsync((string Key, ReadOnlyMemory<byte> Value)[] items, CancellationToken ct);
    bool TrySetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct, out ValueTask<bool> task);

    ValueTask<bool> DeleteAsync(string key, CancellationToken ct);
    ValueTask<long> TtlSecondsAsync(string key, CancellationToken ct);
    ValueTask<long> PTtlMillisecondsAsync(string key, CancellationToken ct);
    ValueTask<long> UnlinkAsync(string key, CancellationToken ct);

    // Lease-based reads (avoid allocating byte[] on hot paths; caller must Dispose).
    ValueTask<RedisValueLease> GetLeaseAsync(string key, CancellationToken ct);
    ValueTask<RedisValueLease> GetExLeaseAsync(string key, TimeSpan? ttl, CancellationToken ct);
    bool TryGetLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task);
    bool TryGetExLeaseAsync(string key, TimeSpan? ttl, CancellationToken ct, out ValueTask<RedisValueLease> task);

    // Hashes
    ValueTask<long> HSetAsync(string key, string field, ReadOnlyMemory<byte> value, CancellationToken ct);
    ValueTask<byte[]?> HGetAsync(string key, string field, CancellationToken ct);
    ValueTask<byte[]?[]> HMGetAsync(string key, string[] fields, CancellationToken ct);
    ValueTask<RedisValueLease> HGetLeaseAsync(string key, string field, CancellationToken ct);
    bool TryHGetAsync(string key, string field, CancellationToken ct, out ValueTask<byte[]?> task);

    // Lists
    ValueTask<long> LPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct);
    ValueTask<long> RPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct);
    ValueTask<long> RPushManyAsync(string key, ReadOnlyMemory<byte>[] values, int count, CancellationToken ct);
    ValueTask<byte[]?> LPopAsync(string key, CancellationToken ct);
    ValueTask<byte[]?> RPopAsync(string key, CancellationToken ct);
    bool TryLPopAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task);
    bool TryRPopAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task);
    ValueTask<byte[]?[]> LRangeAsync(string key, long start, long stop, CancellationToken ct);
    ValueTask<long> LLenAsync(string key, CancellationToken ct);
    ValueTask<RedisValueLease> LPopLeaseAsync(string key, CancellationToken ct);
    ValueTask<RedisValueLease> RPopLeaseAsync(string key, CancellationToken ct);
    bool TryLPopLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task);
    bool TryRPopLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task);

    // Sets
    ValueTask<long> SAddAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct);
    ValueTask<long> SRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct);
    ValueTask<bool> SIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct);
    ValueTask<byte[]?[]> SMembersAsync(string key, CancellationToken ct);
    ValueTask<long> SCardAsync(string key, CancellationToken ct);
    bool TrySIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct, out ValueTask<bool> task);

    // Sorted Sets
    ValueTask<long> ZAddAsync(string key, double score, ReadOnlyMemory<byte> member, CancellationToken ct);
    ValueTask<long> ZRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct);
    ValueTask<long> ZCardAsync(string key, CancellationToken ct);
    ValueTask<double?> ZScoreAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct);
    ValueTask<long?> ZRankAsync(string key, ReadOnlyMemory<byte> member, bool descending, CancellationToken ct);
    ValueTask<double> ZIncrByAsync(string key, double increment, ReadOnlyMemory<byte> member, CancellationToken ct);
    ValueTask<(byte[] Member, double Score)[]> ZRangeWithScoresAsync(string key, long start, long stop, bool descending, CancellationToken ct);
    ValueTask<(byte[] Member, double Score)[]> ZRangeByScoreWithScoresAsync(
        string key,
        double min,
        double max,
        bool descending,
        long? offset,
        long? count,
        CancellationToken ct);

    // JSON (RedisJSON module)
    ValueTask<byte[]?> JsonGetAsync(string key, string? path, CancellationToken ct);
    // Lease-based JSON GET (avoid allocating byte[] on hot paths; caller must Dispose).
    ValueTask<RedisValueLease> JsonGetLeaseAsync(string key, string? path, CancellationToken ct);
    bool TryJsonGetLeaseAsync(string key, string? path, CancellationToken ct, out ValueTask<RedisValueLease> task);
    ValueTask<bool> JsonSetAsync(string key, string? path, ReadOnlyMemory<byte> json, CancellationToken ct);
    ValueTask<bool> JsonSetLeaseAsync(string key, string? path, RedisValueLease json, CancellationToken ct);
    ValueTask<long> JsonDelAsync(string key, string? path, CancellationToken ct);

    // RediSearch (FT.*)
    ValueTask<bool> FtCreateAsync(string index, string prefix, string[] fields, CancellationToken ct);
    ValueTask<string[]> FtSearchAsync(string index, string query, int? offset, int? count, CancellationToken ct);

    // RedisBloom (BF.*)
    ValueTask<bool> BfAddAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct);
    ValueTask<bool> BfExistsAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct);

    // RedisTimeSeries (TS.*)
    ValueTask<bool> TsCreateAsync(string key, CancellationToken ct);
    ValueTask<long> TsAddAsync(string key, long timestamp, double value, CancellationToken ct);
    ValueTask<(long Timestamp, double Value)[]> TsRangeAsync(string key, long from, long to, CancellationToken ct);

    // Scan/streaming
    IAsyncEnumerable<string> ScanAsync(string? pattern = null, int pageSize = 128, CancellationToken ct = default);
    IAsyncEnumerable<byte[]> SScanAsync(string key, string? pattern = null, int pageSize = 128, CancellationToken ct = default);
    IAsyncEnumerable<(string Field, byte[] Value)> HScanAsync(string key, string? pattern = null, int pageSize = 128, CancellationToken ct = default);
    IAsyncEnumerable<(byte[] Member, double Score)> ZScanAsync(string key, string? pattern = null, int pageSize = 128, CancellationToken ct = default);

    // Server commands
    ValueTask<string> PingAsync(CancellationToken ct);
    ValueTask<string[]> ModuleListAsync(CancellationToken ct);
}
