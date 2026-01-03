using VapeCache.Reconciliation;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.Reconciliation;

public sealed class RedisReconciliationStoreSelectorTests
{
    [Fact]
    public async Task Selector_UsesInMemory_WhenConfigured()
    {
        var options = new RedisReconciliationStoreOptions { UseSqlite = false };
        var selector = BuildSelector(options, out _);

        await selector.TryUpsertWriteAsync("k", new byte[] { 1 }, DateTimeOffset.UtcNow, null, CancellationToken.None);
        var count = await selector.CountAsync(CancellationToken.None);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Selector_UsesSqlite_WhenConfigured()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"vapecache-recon-{Guid.NewGuid():N}.db");
        var options = new RedisReconciliationStoreOptions { UseSqlite = true, StorePath = temp };
        var selector = BuildSelector(options, out var sqlitePath);

        await selector.TryUpsertWriteAsync("k", new byte[] { 2 }, DateTimeOffset.UtcNow, null, CancellationToken.None);
        var count = await selector.CountAsync(CancellationToken.None);

        Assert.Equal(1, count);
        try
        {
            if (File.Exists(sqlitePath))
                File.Delete(sqlitePath);
        }
        catch (IOException)
        {
            // Best-effort cleanup; SQLite may keep the file handle open briefly.
        }
    }

    private static RedisReconciliationStoreSelector BuildSelector(RedisReconciliationStoreOptions options, out string sqlitePath)
    {
        sqlitePath = options.StorePath ?? string.Empty;
        var monitor = new TestOptionsMonitor<RedisReconciliationStoreOptions>(options);
        var sqlite = new SqliteReconciliationStore(options);
        var memory = new InMemoryReconciliationStore();
        return new RedisReconciliationStoreSelector(monitor, sqlite, memory);
    }
}
