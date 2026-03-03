namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Shared naming conventions for cache tags.
/// </summary>
public static class CacheTagConventions
{
    /// <summary>
    /// Reserved prefix used to represent cache zones as tags.
    /// </summary>
    public const string ZonePrefix = "zone:";

    /// <summary>
    /// Converts a zone name into its reserved tag representation.
    /// </summary>
    public static string ToZoneTag(string zone)
    {
        if (string.IsNullOrWhiteSpace(zone))
            throw new ArgumentException("Zone must not be null or whitespace.", nameof(zone));

        return string.Concat(ZonePrefix, zone.Trim());
    }
}
