using VapeCache.Core.Policies;

namespace VapeCache.Tests.Core;

public sealed class CacheTagPolicyTests
{
    [Fact]
    public void ToZoneTag_trims_and_applies_zone_prefix()
    {
        var zoneTag = CacheTagPolicy.ToZoneTag("  grocery:inventory  ");

        Assert.Equal("zone:grocery:inventory", zoneTag);
    }

    [Fact]
    public void ToZoneTag_throws_for_whitespace_input()
    {
        Assert.Throws<ArgumentException>(() => CacheTagPolicy.ToZoneTag("   "));
    }

    [Fact]
    public void NormalizeTag_returns_same_reference_for_already_normalized_input()
    {
        const string tag = "tenant:1";

        var normalized = CacheTagPolicy.NormalizeTag(tag);

        Assert.Same(tag, normalized);
    }

    [Fact]
    public void NormalizeTag_trims_surrounding_whitespace()
    {
        var normalized = CacheTagPolicy.NormalizeTag("  tenant:1  ");

        Assert.Equal("tenant:1", normalized);
    }

    [Fact]
    public void NormalizeTags_returns_empty_when_all_inputs_are_missing_or_whitespace()
    {
        var normalized = CacheTagPolicy.NormalizeTags(
            existingTags: [" ", "\t", null!],
            additionalTags: [null!, ""]);

        Assert.Empty(normalized);
    }

    [Fact]
    public void NormalizeTags_preserves_order_and_removes_duplicates()
    {
        var normalized = CacheTagPolicy.NormalizeTags(
            existingTags: ["tenant:1", " shared ", "", "catalog"],
            additionalTags: ["shared", "new", "tenant:1", "  new  "]);

        Assert.Equal(["tenant:1", "shared", "catalog", "new"], normalized);
    }

    [Fact]
    public void NormalizeTags_handles_large_inputs_with_stable_output()
    {
        var normalized = CacheTagPolicy.NormalizeTags(
            existingTags: ["a0", "a1", "a2", "a3", "a4"],
            additionalTags: ["a4", "a5", "a6", "a7", "a8", "a1", "a9", "a10"]);

        Assert.Equal(["a0", "a1", "a2", "a3", "a4", "a5", "a6", "a7", "a8", "a9", "a10"], normalized);
    }
}
