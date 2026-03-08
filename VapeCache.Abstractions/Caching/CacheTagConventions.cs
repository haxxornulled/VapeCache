namespace VapeCache.Abstractions.Caching;

using VapeCache.Core.Policies;

/// <summary>
/// Shared naming conventions for cache tags.
/// </summary>
public static class CacheTagConventions
{
    /// <summary>
    /// Reserved prefix used to represent cache zones as tags.
    /// </summary>
    public const string ZonePrefix = CacheTagPolicy.ZonePrefix;

    /// <summary>
    /// Converts a zone name into its reserved tag representation.
    /// </summary>
    public static string ToZoneTag(string zone)
        => CacheTagPolicy.ToZoneTag(zone);
}
