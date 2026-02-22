using Microsoft.Data.Sqlite;
using System.Data;

namespace VapeCache.Reconciliation;

internal sealed class SqliteReconciliationStore : IRedisReconciliationStore
{
    private readonly string _connectionString;
    private readonly RedisReconciliationStoreOptions _options;
    private int _initialized;
    private readonly SemaphoreSlim _initGate = new(1, 1);

    public SqliteReconciliationStore(Microsoft.Extensions.Options.IOptionsMonitor<RedisReconciliationStoreOptions> options)
        : this(options.CurrentValue)
    {
    }

    public SqliteReconciliationStore(RedisReconciliationStoreOptions options)
    {
        _options = options;
        var path = ResolvePath(options.StorePath);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Cache = SqliteCacheMode.Shared,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        _connectionString = builder.ToString();
    }

    public async ValueTask<int> CountAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = OpenConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM reconciliation_ops";
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }

    public async ValueTask<bool> TryUpsertWriteAsync(string key, ReadOnlyMemory<byte> value, DateTimeOffset trackedAt, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await RetryOnBusyAsync(async () =>
        {
            await using var conn = OpenConnection();
            await conn.OpenAsync(ct).ConfigureAwait(false);

            var trackedUnix = trackedAt.ToUnixTimeMilliseconds();
            var expiresUnix = expiresAt.HasValue ? expiresAt.Value.ToUnixTimeMilliseconds() : (long?)null;
            var payload = value.ToArray();

            await using var insert = conn.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO reconciliation_ops(key, type, value, expires_at, tracked_at) " +
                                 "VALUES ($key, $type, $value, $expires, $tracked)";
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$type", (int)OperationType.Write);
            insert.Parameters.AddWithValue("$value", payload);
            insert.Parameters.AddWithValue("$expires", expiresUnix.HasValue ? expiresUnix.Value : DBNull.Value);
            insert.Parameters.AddWithValue("$tracked", trackedUnix);
            var inserted = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;

            if (!inserted)
            {
                await using var update = conn.CreateCommand();
                update.CommandText = "UPDATE reconciliation_ops " +
                                     "SET type=$type, value=$value, expires_at=$expires, tracked_at=$tracked " +
                                     "WHERE key=$key";
                update.Parameters.AddWithValue("$key", key);
                update.Parameters.AddWithValue("$type", (int)OperationType.Write);
                update.Parameters.AddWithValue("$value", payload);
                update.Parameters.AddWithValue("$expires", expiresUnix.HasValue ? expiresUnix.Value : DBNull.Value);
                update.Parameters.AddWithValue("$tracked", trackedUnix);
                await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            return inserted;
        }, ct).ConfigureAwait(false);
    }

    public async ValueTask<bool> TryUpsertDeleteAsync(string key, DateTimeOffset trackedAt, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await RetryOnBusyAsync(async () =>
        {
            await using var conn = OpenConnection();
            await conn.OpenAsync(ct).ConfigureAwait(false);

            var trackedUnix = trackedAt.ToUnixTimeMilliseconds();

            await using var insert = conn.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO reconciliation_ops(key, type, value, expires_at, tracked_at) " +
                                 "VALUES ($key, $type, NULL, NULL, $tracked)";
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$type", (int)OperationType.Delete);
            insert.Parameters.AddWithValue("$tracked", trackedUnix);
            var inserted = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;

            if (!inserted)
            {
                await using var update = conn.CreateCommand();
                update.CommandText = "UPDATE reconciliation_ops " +
                                     "SET type=$type, value=NULL, expires_at=NULL, tracked_at=$tracked " +
                                     "WHERE key=$key";
                update.Parameters.AddWithValue("$key", key);
                update.Parameters.AddWithValue("$type", (int)OperationType.Delete);
                update.Parameters.AddWithValue("$tracked", trackedUnix);
                await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            return inserted;
        }, ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<TrackedOperation>> SnapshotAsync(int maxOperations, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = OpenConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, type, value, expires_at, tracked_at FROM reconciliation_ops ORDER BY tracked_at LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", maxOperations <= 0 ? -1 : maxOperations);

        var list = new List<TrackedOperation>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var key = reader.GetString(0);
            var type = (OperationType)reader.GetInt32(1);
            byte[]? value = reader.IsDBNull(2) ? null : reader.GetFieldValue<byte[]>(2);
            DateTimeOffset? expiresAt = reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3));
            var trackedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4));

            list.Add(new TrackedOperation
            {
                Key = key,
                Type = type,
                Value = value,
                ExpiresAt = expiresAt,
                TrackedAt = trackedAt
            });
        }

        return list;
    }

    public async ValueTask RemoveAsync(IReadOnlyList<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0) return;
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = OpenConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        // SQLite has a parameter limit of 999 (SQLITE_MAX_VARIABLE_NUMBER)
        // Batch deletes in chunks to avoid exceeding the limit
        const int MaxParametersPerQuery = 999;

        for (int offset = 0; offset < keys.Count; offset += MaxParametersPerQuery)
        {
            var batchSize = Math.Min(MaxParametersPerQuery, keys.Count - offset);
            var placeholders = string.Join(",", Enumerable.Range(0, batchSize).Select(i => $"$key{i}"));

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM reconciliation_ops WHERE key IN ({placeholders})";

            for (int i = 0; i < batchSize; i++)
            {
                cmd.Parameters.AddWithValue($"$key{i}", keys[offset + i]);
            }

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask ClearAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = OpenConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM reconciliation_ops";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        if (_options.VacuumOnClear)
        {
            await using var vacuum = conn.CreateCommand();
            vacuum.CommandText = "VACUUM";
            await vacuum.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _initialized) == 1) return;

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _initialized) == 1) return;

            var dir = Path.GetDirectoryName(new SqliteConnectionStringBuilder(_connectionString).DataSource);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            await using var conn = OpenConnection();
            await conn.OpenAsync(ct).ConfigureAwait(false);

            if (_options.EnablePragmaOptimizations)
            {
                await using var pragma = conn.CreateCommand();
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS reconciliation_ops (
  key TEXT PRIMARY KEY,
  type INTEGER NOT NULL,
  value BLOB NULL,
  expires_at INTEGER NULL,
  tracked_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_reconciliation_tracked ON reconciliation_ops(tracked_at);";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            Volatile.Write(ref _initialized, 1);
        }
        finally
        {
            _initGate.Release();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        // DefaultTimeout is in seconds, so convert from milliseconds
        var timeoutSeconds = Math.Max(1, (int)Math.Ceiling(_options.BusyTimeoutMs / 1000.0));
        conn.DefaultTimeout = timeoutSeconds;
        return conn;
    }

    private static string ResolvePath(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));

        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "VapeCache", "persistence", "reconciliation.db");
    }

    /// <summary>
    /// Retries SQLite operations on SQLITE_BUSY errors with exponential backoff.
    /// Critical for production-grade reliability under concurrent load.
    /// </summary>
    private static async ValueTask<T> RetryOnBusyAsync<T>(Func<ValueTask<T>> operation, CancellationToken ct, int maxRetries = 3)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5 && attempt < maxRetries) // SQLITE_BUSY = 5
            {
                attempt++;
                var delayMs = attempt * attempt * 10; // Exponential backoff: 10ms, 40ms, 90ms
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        }
    }
}
