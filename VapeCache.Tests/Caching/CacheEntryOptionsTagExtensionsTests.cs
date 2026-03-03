using VapeCache.Abstractions.Caching;

namespace VapeCache.Tests.Caching;

public sealed class CacheEntryOptionsTagExtensionsTests
{
    [Fact]
    public void WithTags_merges_existing_and_new_tags_without_duplicates()
    {
        var options = new CacheEntryOptions(
            Ttl: TimeSpan.FromMinutes(1),
            Intent: new CacheIntent(
                CacheIntentKind.QueryResult,
                Tags: ["existing", "shared"]));

        var merged = options.WithTags("shared", "new");

        Assert.NotNull(merged.Intent);
        Assert.Equal(["existing", "shared", "new"], merged.Intent!.Tags);
    }

    [Fact]
    public void WithZone_adds_reserved_zone_tag()
    {
        var options = new CacheEntryOptions(TimeSpan.FromMinutes(1))
            .WithZone("ef:products");

        Assert.NotNull(options.Intent);
        Assert.Equal(["zone:ef:products"], options.Intent!.Tags);
    }

    [Fact]
    public void WithZones_merges_with_existing_tags()
    {
        var options = new CacheEntryOptions(
            Intent: new CacheIntent(
                CacheIntentKind.QueryResult,
                Tags: ["tenant:1"]));

        var merged = options.WithZones("ef:products", "ef:inventory");

        Assert.Equal(
            ["tenant:1", "zone:ef:products", "zone:ef:inventory"],
            merged.Intent!.Tags);
    }

    [Fact]
    public void WithZone_throws_for_invalid_input()
    {
        var options = new CacheEntryOptions(TimeSpan.FromMinutes(1));
        Assert.Throws<ArgumentException>(() => options.WithZone(" "));
    }
}
