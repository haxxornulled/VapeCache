using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VapeCache.Features.Search;

/// <summary>
/// Shared key and tag conventions for search projections and cached search results.
/// </summary>
public static class RedisSearchConventions
{
    /// <summary>
    /// Gets the shared search zone name for an index.
    /// </summary>
    public static string SearchZone(string indexName)
        => $"search:{NormalizeSegment(indexName)}";

    /// <summary>
    /// Gets the broad invalidation tag for an index.
    /// </summary>
    public static string IndexTag(string indexName)
        => SearchZone(indexName);

    /// <summary>
    /// Gets a scoped invalidation tag for an index.
    /// </summary>
    public static string ScopeTag(string indexName, string scope, string value)
        => $"{SearchZone(indexName)}:{NormalizeSegment(scope)}:{NormalizeSegment(value)}";

    /// <summary>
    /// Gets an entity-specific invalidation tag for an index.
    /// </summary>
    public static string EntityTag(string indexName, string entityName, string entityId)
        => ScopeTag(indexName, entityName, entityId);

    /// <summary>
    /// Builds a deterministic cache key for a query result page.
    /// </summary>
    public static string QueryCacheKey(string indexName, RedisSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var material = string.Concat(
            indexName, "\n",
            query.RawQuery, "\n",
            query.Offset?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, "\n",
            query.Count?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return $"{SearchZone(indexName)}:query:{hash}";
    }

    /// <summary>
    /// Builds a HASH document key from a prefix and document id.
    /// </summary>
    public static string DocumentKey(string documentKeyPrefix, string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKeyPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        return string.Concat(documentKeyPrefix, documentId);
    }

    private static string NormalizeSegment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}
