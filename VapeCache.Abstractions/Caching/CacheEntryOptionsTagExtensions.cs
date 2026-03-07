namespace VapeCache.Abstractions.Caching;

using VapeCache.Core.Policies;

/// <summary>
/// Helpers for applying tag metadata to cache entry options.
/// </summary>
public static class CacheEntryOptionsTagExtensions
{
    /// <summary>
    /// Applies one tag to the cache entry options.
    /// </summary>
    public static CacheEntryOptions WithTag(this CacheEntryOptions options, string tag)
        => options.WithTags([tag]);

    /// <summary>
    /// Applies tags to the cache entry options.
    /// Existing intent metadata is preserved.
    /// </summary>
    public static CacheEntryOptions WithTags(this CacheEntryOptions options, params string[] tags)
    {
        var existingTags = options.Intent?.Tags;
        var normalized = CacheTagPolicy.NormalizeTags(existingTags, tags);
        if (normalized.Length == 0)
            return options;
        if (TagsMatch(existingTags, normalized))
            return options;

        var intent = options.Intent ?? new CacheIntent(
            CacheIntentKind.Unspecified,
            Reason: "tagged-entry");

        var merged = intent with { Tags = normalized };
        return options with { Intent = merged };
    }

    /// <summary>
    /// Applies one cache zone to the cache entry options.
    /// Zone invalidation is backed by reserved tag names.
    /// </summary>
    public static CacheEntryOptions WithZone(this CacheEntryOptions options, string zone)
        => options.WithTag(CacheTagPolicy.ToZoneTag(zone));

    /// <summary>
    /// Applies cache zones to the cache entry options.
    /// Zone names are normalized and converted to reserved tag names.
    /// </summary>
    public static CacheEntryOptions WithZones(this CacheEntryOptions options, params string[] zones)
    {
        if (zones is null || zones.Length == 0)
            return options;
        if (zones.Length == 1)
            return options.WithTag(CacheTagPolicy.ToZoneTag(zones[0]));

        var zoneTags = new string[zones.Length];
        for (var i = 0; i < zones.Length; i++)
            zoneTags[i] = CacheTagPolicy.ToZoneTag(zones[i]);

        return options.WithTags(zoneTags);
    }

    private static bool TagsMatch(string[]? existingTags, string[] normalizedTags)
    {
        if (existingTags is null || existingTags.Length != normalizedTags.Length)
            return false;

        for (var i = 0; i < normalizedTags.Length; i++)
        {
            if (!string.Equals(existingTags[i], normalizedTags[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
