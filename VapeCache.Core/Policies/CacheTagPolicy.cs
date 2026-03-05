namespace VapeCache.Core.Policies;

/// <summary>
/// Canonical policy for cache tag normalization and reserved zone tags.
/// </summary>
public static class CacheTagPolicy
{
    public const string ZonePrefix = "zone:";

    public static string ToZoneTag(string zone)
    {
        if (string.IsNullOrWhiteSpace(zone))
            throw new ArgumentException("Zone must not be null or whitespace.", nameof(zone));

        return string.Concat(ZonePrefix, zone.Trim());
    }

    public static string[] NormalizeTags(string[]? existingTags, string[]? additionalTags)
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
