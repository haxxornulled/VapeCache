using VapeCache.Features.Search;

namespace VapeCache.Tests.Search;

public sealed class RedisSearchQueryBuilderTests
{
    [Fact]
    public void Build_defaults_to_match_all()
    {
        var query = new RedisSearchQueryBuilder().Build();

        Assert.Equal("*", query.RawQuery);
        Assert.Null(query.Offset);
        Assert.Null(query.Count);
    }

    [Fact]
    public void Build_combines_text_tag_and_numeric_clauses()
    {
        var query = new RedisSearchQueryBuilder()
            .MatchText("organic milk")
            .Tag("shopperId", "shopper-42")
            .Tag("receiptStatus", "cleared", "flagged")
            .NumericRange("checkedOutTicks", min: 1000, max: 2000)
            .Build(offset: 10, count: 25);

        Assert.Equal("organic* milk* @shopperId:{shopper-42} @receiptStatus:{cleared|flagged} @checkedOutTicks:[1000 2000]", query.RawQuery);
        Assert.Equal(10, query.Offset);
        Assert.Equal(25, query.Count);
    }

    [Fact]
    public void Query_cache_key_is_deterministic()
    {
        var query = new RedisSearchQueryBuilder()
            .Tag("orderId", "order-100")
            .Build(offset: 0, count: 10);

        var first = RedisSearchConventions.QueryCacheKey("idx:grocery:receipts", query);
        var second = RedisSearchConventions.QueryCacheKey("idx:grocery:receipts", query);

        Assert.Equal(first, second);
        Assert.StartsWith("search:idx:grocery:receipts:query:", first, StringComparison.Ordinal);
    }
}
