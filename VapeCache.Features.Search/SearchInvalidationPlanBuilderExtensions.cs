using VapeCache.Features.Invalidation;

namespace VapeCache.Features.Search;

/// <summary>
/// Invalidation plan helpers for search projections and cached search results.
/// </summary>
public static class SearchInvalidationPlanBuilderExtensions
{
    /// <summary>
    /// Adds the broad zone for a search index.
    /// </summary>
    public static CacheInvalidationPlanBuilder AddSearchZone(this CacheInvalidationPlanBuilder builder, string indexName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddZones([RedisSearchConventions.SearchZone(indexName)]);
    }

    /// <summary>
    /// Adds the broad invalidation tag for a search index.
    /// </summary>
    public static CacheInvalidationPlanBuilder AddSearchIndexTag(this CacheInvalidationPlanBuilder builder, string indexName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddTags([RedisSearchConventions.IndexTag(indexName)]);
    }

    /// <summary>
    /// Adds a scoped invalidation tag for a search index.
    /// </summary>
    public static CacheInvalidationPlanBuilder AddSearchScopeTag(
        this CacheInvalidationPlanBuilder builder,
        string indexName,
        string scope,
        string value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddTags([RedisSearchConventions.ScopeTag(indexName, scope, value)]);
    }

    /// <summary>
    /// Adds an entity-specific invalidation tag for a search index.
    /// </summary>
    public static CacheInvalidationPlanBuilder AddSearchEntityTag(
        this CacheInvalidationPlanBuilder builder,
        string indexName,
        string entityName,
        string entityId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddTags([RedisSearchConventions.EntityTag(indexName, entityName, entityId)]);
    }

    /// <summary>
    /// Adds a direct HASH document key to invalidate.
    /// </summary>
    public static CacheInvalidationPlanBuilder AddSearchDocumentKey(
        this CacheInvalidationPlanBuilder builder,
        string documentKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddKeys([documentKey]);
    }

    /// <summary>
    /// Adds a cached query result key to invalidate.
    /// </summary>
    public static CacheInvalidationPlanBuilder AddSearchQueryCacheKey(
        this CacheInvalidationPlanBuilder builder,
        string indexName,
        RedisSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(query);
        return builder.AddKeys([RedisSearchConventions.QueryCacheKey(indexName, query)]);
    }
}
