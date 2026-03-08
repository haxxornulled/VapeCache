using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VapeCache.Abstractions.Modules;
using VapeCache.Console.Pos;
using VapeCache.Infrastructure.Connections;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.Console;

public sealed class PosCatalogSearchServiceTests
{
    [Fact]
    public async Task Sqlite_store_seeds_and_supports_code_lookup()
    {
        var (store, _, path) = CreateStore(seedCount: 64);
        try
        {
            await store.EnsureInitializedAsync(default);
            await store.SeedIfEmptyAsync(default);

            var byCode = await store.SearchAsync("code:PCL-0001", 5, default);
            Assert.NotEmpty(byCode);
            Assert.Contains(byCode, static p => p.Code == "PCL-0001");

            var byUpc = await store.SearchAsync("upc:012345678901", 5, default);
            Assert.NotEmpty(byUpc);
            Assert.Contains(byUpc, static p => p.Upc == "012345678901");
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Search_path_uses_cache_before_database_and_backfills_on_miss()
    {
        var (store, optionsMonitor, path) = CreateStore(seedCount: 128);
        var search = new FakeSearchService { Available = true };
        await using var redis = new InMemoryCommandExecutor();
        var sut = new PosCatalogSearchService(store, search, redis, optionsMonitor, NullLogger<PosCatalogSearchService>.Instance);

        try
        {
            var miss = await sut.SearchAsync("pencil", default);
            Assert.Equal(PosSearchSource.Database, miss.Source);
            Assert.NotEmpty(miss.Products);

            var pencil = miss.Products.First(static p => p.Code == "PCL-0001");
            var key = $"{optionsMonitor.CurrentValue.RedisKeyPrefix}{pencil.Sku}";

            var hash = await redis.HMGetAsync(key, ["code", "name"], default);
            Assert.Equal("PCL-0001", Encoding.UTF8.GetString(hash[0]!));
            Assert.Contains("Pencil", Encoding.UTF8.GetString(hash[1]!));

            Assert.NotNull(search.LastQuery);
            search.QueryResults[search.LastQuery!] = [key];

            var hit = await sut.SearchAsync("pencil", default);
            Assert.Equal(PosSearchSource.Cache, hit.Source);
            Assert.Contains(hit.Products, p => p.Sku == pencil.Sku);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Code_lookup_can_hit_cache_after_backfill()
    {
        var (store, optionsMonitor, path) = CreateStore(seedCount: 128);
        var search = new FakeSearchService { Available = true };
        await using var redis = new InMemoryCommandExecutor();
        var sut = new PosCatalogSearchService(store, search, redis, optionsMonitor, NullLogger<PosCatalogSearchService>.Instance);

        try
        {
            var first = await sut.SearchAsync("code:PCL-0001", default);
            Assert.Equal(PosSearchSource.Database, first.Source);
            Assert.NotEmpty(first.Products);

            var key = $"{optionsMonitor.CurrentValue.RedisKeyPrefix}{first.Products[0].Sku}";
            Assert.NotNull(search.LastQuery);
            Assert.Contains("@code", search.LastQuery!, StringComparison.Ordinal);
            search.QueryResults[search.LastQuery!] = [key];

            var second = await sut.SearchAsync("code:PCL-0001", default);
            Assert.Equal(PosSearchSource.Cache, second.Source);
            Assert.Single(second.Products);
            Assert.Equal("PCL-0001", second.Products[0].Code);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static (SqlitePosCatalogStore Store, TestOptionsMonitor<PosSearchDemoOptions> Options, string DbPath) CreateStore(int seedCount)
    {
        var root = Path.Combine(Path.GetTempPath(), "vapecache-pos-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "catalog.db");
        var options = new PosSearchDemoOptions
        {
            Enabled = true,
            SqlitePath = dbPath,
            SeedIfEmpty = true,
            SeedProductCount = seedCount,
            RedisIndexName = "idx:test:pos",
            RedisKeyPrefix = "pos:test:sku:",
            TopResults = 10
        };
        var monitor = new TestOptionsMonitor<PosSearchDemoOptions>(options);
        var store = new SqlitePosCatalogStore(monitor, NullLogger<SqlitePosCatalogStore>.Instance);
        return (store, monitor, root);
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
            // Best effort cleanup.
        }
    }

    private sealed class FakeSearchService : IRedisSearchService
    {
        public bool Available { get; init; }
        public Dictionary<string, string[]> QueryResults { get; } = new(StringComparer.Ordinal);
        public string? LastQuery { get; private set; }

        public ValueTask<bool> IsAvailableAsync(CancellationToken ct = default)
            => ValueTask.FromResult(Available);

        public ValueTask<bool> CreateIndexAsync(string index, string prefix, string[] fields, CancellationToken ct = default)
            => ValueTask.FromResult(Available);

        public ValueTask<string[]> SearchAsync(string index, string query, int? offset = null, int? count = null, CancellationToken ct = default)
        {
            LastQuery = query;
            return ValueTask.FromResult(QueryResults.TryGetValue(query, out var ids) ? ids : Array.Empty<string>());
        }
    }
}
