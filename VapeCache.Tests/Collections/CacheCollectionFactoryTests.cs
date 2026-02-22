using VapeCache.Infrastructure.Caching.Codecs;
using VapeCache.Infrastructure.Collections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Collections;

public sealed class CacheCollectionFactoryTests
{
    [Fact]
    public async Task Factory_creates_collections_that_share_executor()
    {
        await using var executor = new InMemoryCommandExecutor();
        var codecs = new SystemTextJsonCodecProvider();
        var sut = new CacheCollectionFactory(executor, codecs);

        var list = sut.List<string>("list:k");
        await list.PushFrontAsync("one");
        var listValues = await list.RangeAsync(0, -1);
        Assert.Single(listValues);
        Assert.Equal("one", listValues[0]);

        var set = sut.Set<int>("set:k");
        await set.AddAsync(10);
        Assert.True(await set.ContainsAsync(10));

        var hash = sut.Hash<string>("hash:k");
        await hash.SetAsync("field", "value");
        var h = await hash.GetAsync("field");
        Assert.Equal("value", h);

        var sorted = sut.SortedSet<string>("z:k");
        await sorted.AddAsync("member", 1.5);
        var z = await sorted.RangeByRankAsync(0, -1);
        Assert.Single(z);
        Assert.Equal("member", z[0].Member);
    }
}
