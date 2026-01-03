using System;
using System.IO;
using System.Threading.Tasks;
using VapeCache.Reconciliation;
using Xunit;

namespace VapeCache.Tests.Caching;

public sealed class RedisReconciliationSqliteStoreTests
{
    [Fact]
    public async Task StorePath_ExpandsEnvironmentVariables_AndCreatesFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "vapecache-recon", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var envVar = "VAPECACHE_RECON_TEST_PATH";
        Environment.SetEnvironmentVariable(envVar, root);

        var configuredPath = $"%{envVar}%{Path.DirectorySeparatorChar}persistence{Path.DirectorySeparatorChar}reconciliation.db";
        var expectedPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath));

        var options = new RedisReconciliationStoreOptions
        {
            UseSqlite = true,
            StorePath = configuredPath
        };

        var store = new SqliteReconciliationStore(options);
        try
        {
            var count = await store.CountAsync(default);
            Assert.Equal(0, count);
            Assert.True(File.Exists(expectedPath), $"Expected SQLite file at {expectedPath}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Store_AllowsConcurrentWriters()
    {
        var root = Path.Combine(Path.GetTempPath(), "vapecache-recon", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "reconciliation.db");

        var options = new RedisReconciliationStoreOptions
        {
            UseSqlite = true,
            StorePath = dbPath,
            BusyTimeoutMs = 1000
        };

        var storeA = new SqliteReconciliationStore(options);
        var storeB = new SqliteReconciliationStore(options);

        try
        {
            var writeA = storeA.TryUpsertWriteAsync("k1", new byte[] { 1 }, DateTimeOffset.UtcNow, null, default);
            var writeB = storeB.TryUpsertWriteAsync("k2", new byte[] { 2 }, DateTimeOffset.UtcNow, null, default);
            await Task.WhenAll(writeA.AsTask(), writeB.AsTask());

            var ops = await storeA.SnapshotAsync(10, default);
            Assert.Contains(ops, op => op.Key == "k1");
            Assert.Contains(ops, op => op.Key == "k2");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Store_InitializesOnce_WithConcurrentCalls()
    {
        var root = Path.Combine(Path.GetTempPath(), "vapecache-recon", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "reconciliation.db");

        var options = new RedisReconciliationStoreOptions
        {
            UseSqlite = true,
            StorePath = dbPath,
            BusyTimeoutMs = 1000
        };

        var store = new SqliteReconciliationStore(options);

        try
        {
            var tasks = new Task[20];
            for (var i = 0; i < tasks.Length; i++)
            {
                var id = i;
                tasks[i] = Task.Run(async () =>
                {
                    if ((id % 2) == 0)
                        _ = await store.CountAsync(default);
                    else
                        await store.TryUpsertWriteAsync($"k{id}", new byte[] { (byte)id }, DateTimeOffset.UtcNow, null, default);
                });
            }

            await Task.WhenAll(tasks);
            var count = await store.CountAsync(default);
            Assert.True(count >= 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for tests.
        }
    }
}
