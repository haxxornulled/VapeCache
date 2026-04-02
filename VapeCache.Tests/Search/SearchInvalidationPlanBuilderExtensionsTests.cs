using VapeCache.Features.Invalidation;
using VapeCache.Features.Search;

namespace VapeCache.Tests.Search;

public sealed class SearchInvalidationPlanBuilderExtensionsTests
{
    [Fact]
    public void Search_helpers_add_expected_targets()
    {
        var query = new RedisSearchQueryBuilder()
            .Tag("shopperId", "shopper-42")
            .Build(offset: 0, count: 10);

        var plan = new CacheInvalidationPlanBuilder()
            .AddSearchZone("idx:grocery:receipts")
            .AddSearchIndexTag("idx:grocery:receipts")
            .AddSearchScopeTag("idx:grocery:receipts", "shopperId", "shopper-42")
            .AddSearchEntityTag("idx:grocery:receipts", "order", "order-100")
            .AddSearchDocumentKey("receipt:search:doc:order-100")
            .AddSearchQueryCacheKey("idx:grocery:receipts", query)
            .Build();

        Assert.Contains("search:idx:grocery:receipts", plan.Zones);
        Assert.Contains("search:idx:grocery:receipts", plan.Tags);
        Assert.Contains("search:idx:grocery:receipts:shopperId:shopper-42", plan.Tags);
        Assert.Contains("search:idx:grocery:receipts:order:order-100", plan.Tags);
        Assert.Contains("receipt:search:doc:order-100", plan.Keys);
        Assert.Contains(plan.Keys, static key => key.StartsWith("search:idx:grocery:receipts:query:", StringComparison.Ordinal));
    }
}
