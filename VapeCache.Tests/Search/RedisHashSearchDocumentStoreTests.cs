using System.Globalization;
using System.Text;
using VapeCache.Abstractions.Modules;
using VapeCache.Features.Search;
using VapeCache.Infrastructure.Connections;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.Search;

public sealed class RedisHashSearchDocumentStoreTests
{
    [Fact]
    public async Task EnsureIndex_uses_explicit_schema_and_search_defaults()
    {
        var search = new FakeSearchService { Available = true };
        await using var redis = new InMemoryCommandExecutor();
        var mapper = new ReceiptSearchMapper();
        var options = new TestOptionsMonitor<VapeCacheSearchOptions>(new VapeCacheSearchOptions
        {
            Enabled = true,
            DefaultResultCount = 50
        });
        using var sut = new RedisHashSearchDocumentStore<ReceiptSearchDocument>(redis, search, mapper, options);

        var ensured = await sut.EnsureIndexAsync();
        var query = new RedisSearchQueryBuilder()
            .Tag("shopperId", "shopper-42")
            .Build();

        search.QueryResults["idx:grocery:receipts|@shopperId:{shopper-42}|0|50"] = ["receipt:search:doc:order-100"];

        var ids = await sut.SearchIdsAsync(query);
        var count = await sut.SearchCountAsync(query);

        Assert.True(ensured);
        Assert.NotNull(search.CreatedFields);
        Assert.Collection(
            search.CreatedFields!,
            field =>
            {
                Assert.Equal("orderId", field.Name);
                Assert.Equal(RedisSearchFieldType.Tag, field.Type);
                Assert.True(field.Sortable);
            },
            field => Assert.Equal(RedisSearchFieldType.Tag, field.Type),
            field => Assert.Equal(RedisSearchFieldType.Numeric, field.Type),
            field =>
            {
                Assert.Equal("searchText", field.Name);
                Assert.Equal(RedisSearchFieldType.Text, field.Type);
                Assert.Equal(2.0, field.Weight);
            });
        Assert.Single(ids);
        Assert.Equal(1L, count);
    }

    [Fact]
    public async Task UpsertAsync_materializes_hash_projection()
    {
        var search = new FakeSearchService { Available = true };
        await using var redis = new InMemoryCommandExecutor();
        var mapper = new ReceiptSearchMapper();
        var options = new TestOptionsMonitor<VapeCacheSearchOptions>(new VapeCacheSearchOptions());
        using var sut = new RedisHashSearchDocumentStore<ReceiptSearchDocument>(redis, search, mapper, options);

        var key = await sut.UpsertAsync(new ReceiptSearchDocument(
            OrderId: "order-200",
            ShopperId: "shopper-9",
            CheckedOutTicks: 123456789L,
            SearchText: "organic milk order-200"), ttl: TimeSpan.FromMinutes(5));

        var values = await redis.HMGetAsync(key, ["orderId", "shopperId", "checkedOutTicks", "searchText"], default);

        Assert.Equal("receipt:search:doc:order-200", key);
        Assert.Equal("order-200", Encoding.UTF8.GetString(values[0]!));
        Assert.Equal("shopper-9", Encoding.UTF8.GetString(values[1]!));
        Assert.Equal("123456789", Encoding.UTF8.GetString(values[2]!));
        Assert.Equal("organic milk order-200", Encoding.UTF8.GetString(values[3]!));
        Assert.True(await redis.TtlSecondsAsync(key, default) > 0);
    }

    [Fact]
    public async Task EnsureIndex_throws_when_module_required_but_missing()
    {
        var search = new FakeSearchService { Available = false };
        await using var redis = new InMemoryCommandExecutor();
        var mapper = new ReceiptSearchMapper();
        var options = new TestOptionsMonitor<VapeCacheSearchOptions>(new VapeCacheSearchOptions
        {
            RequireModuleAvailability = true
        });
        using var sut = new RedisHashSearchDocumentStore<ReceiptSearchDocument>(redis, search, mapper, options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.EnsureIndexAsync().AsTask());
    }

    private sealed record ReceiptSearchDocument(
        string OrderId,
        string ShopperId,
        long CheckedOutTicks,
        string SearchText);

    private sealed class ReceiptSearchMapper : IRedisHashSearchDocumentMapper<ReceiptSearchDocument>
    {
        public RedisSearchIndexDefinition Index { get; } = new(
            "idx:grocery:receipts",
            "receipt:search:doc:",
            [
                RedisSearchFieldDefinition.Tag("orderId", sortable: true),
                RedisSearchFieldDefinition.Tag("shopperId"),
                RedisSearchFieldDefinition.Numeric("checkedOutTicks", sortable: true),
                RedisSearchFieldDefinition.Text("searchText", weight: 2.0)
            ]);

        public string GetDocumentId(ReceiptSearchDocument document) => document.OrderId;

        public IReadOnlyList<RedisSearchHashFieldValue> MapFields(ReceiptSearchDocument document)
            =>
            [
                new("orderId", document.OrderId),
                new("shopperId", document.ShopperId),
                new("checkedOutTicks", document.CheckedOutTicks.ToString(CultureInfo.InvariantCulture)),
                new("searchText", document.SearchText)
            ];
    }

    private sealed class FakeSearchService : IRedisSearchService
    {
        public bool Available { get; init; }
        public IReadOnlyList<RedisSearchFieldDefinition>? CreatedFields { get; private set; }
        public Dictionary<string, string[]> QueryResults { get; } = new(StringComparer.Ordinal);

        public ValueTask<bool> IsAvailableAsync(CancellationToken ct = default)
            => ValueTask.FromResult(Available);

        public ValueTask<bool> CreateIndexAsync(string index, string prefix, string[] fields, CancellationToken ct = default)
        {
            CreatedFields = fields.Select(static field => RedisSearchFieldDefinition.Text(field)).ToArray();
            return ValueTask.FromResult(Available);
        }

        public ValueTask<bool> CreateIndexAsync(
            string index,
            string prefix,
            IReadOnlyList<RedisSearchFieldDefinition> fields,
            CancellationToken ct = default)
        {
            CreatedFields = fields.ToArray();
            return ValueTask.FromResult(Available);
        }

        public ValueTask<string[]> SearchAsync(
            string index,
            string query,
            int? offset = null,
            int? count = null,
            CancellationToken ct = default)
        {
            var key = $"{index}|{query}|{offset?.ToString(CultureInfo.InvariantCulture) ?? "0"}|{count?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}";
            return ValueTask.FromResult(QueryResults.TryGetValue(key, out var ids) ? ids : Array.Empty<string>());
        }

        public ValueTask<long> SearchCountAsync(
            string index,
            string query,
            int? offset = null,
            int? count = null,
            CancellationToken ct = default)
        {
            var key = $"{index}|{query}|{offset?.ToString(CultureInfo.InvariantCulture) ?? "0"}|{count?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}";
            return ValueTask.FromResult(QueryResults.TryGetValue(key, out var ids) ? (long)ids.Length : 0L);
        }
    }
}
