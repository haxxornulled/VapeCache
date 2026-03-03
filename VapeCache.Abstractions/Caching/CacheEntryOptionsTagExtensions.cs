namespace VapeCache.Abstractions.Caching;

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
        var normalized = NormalizeTags(existingTags, tags);
        if (normalized.Length == 0)
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
        => options.WithZones([zone]);

    /// <summary>
    /// Applies cache zones to the cache entry options.
    /// Zone names are normalized and converted to reserved tag names.
    /// </summary>
    public static CacheEntryOptions WithZones(this CacheEntryOptions options, params string[] zones)
    {
        if (zones is null || zones.Length == 0)
            return options;

        var zoneTags = new string[zones.Length];
        for (var i = 0; i < zones.Length; i++)
            zoneTags[i] = CacheTagConventions.ToZoneTag(zones[i]);

        return options.WithTags(zoneTags);
    }

    private static string[] NormalizeTags(string[]? existingTags, string[]? additionalTags)
    {
        var existingCount = existingTags?.Length ?? 0;
        var additionalCount = additionalTags?.Length ?? 0;
        if (existingCount == 0 && additionalCount == 0)
            return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(existingCount + additionalCount);

        AddNormalized(existingTags);
        AddNormalized(additionalTags);
        return result.ToArray();

        void AddNormalized(string[]? tags)
        {
            if (tags is null)
                return;

            foreach (var tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag))
                    continue;

                var normalized = tag.Trim();
                if (seen.Add(normalized))
                    result.Add(normalized);
            }
        }
    }
}
