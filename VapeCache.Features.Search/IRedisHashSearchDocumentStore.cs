namespace VapeCache.Features.Search;

/// <summary>
/// Stores HASH-backed search projections and queries them through RediSearch.
/// </summary>
public interface IRedisHashSearchDocumentStore<TDocument>
{
    /// <summary>
    /// Index definition for the document type.
    /// </summary>
    RedisSearchIndexDefinition Index { get; }

    /// <summary>
    /// Ensures the search index exists and is queryable.
    /// </summary>
    ValueTask<bool> EnsureIndexAsync(CancellationToken ct = default);

    /// <summary>
    /// Upserts a projection document and returns its Redis key.
    /// </summary>
    ValueTask<string> UpsertAsync(TDocument document, TimeSpan? ttl = null, CancellationToken ct = default);

    /// <summary>
    /// Deletes a projection document by identifier.
    /// </summary>
    ValueTask<bool> DeleteAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// Searches the index and returns matching document ids.
    /// </summary>
    ValueTask<string[]> SearchIdsAsync(RedisSearchQuery query, CancellationToken ct = default);

    /// <summary>
    /// Returns the total hit count for a query.
    /// </summary>
    ValueTask<long> SearchCountAsync(RedisSearchQuery query, CancellationToken ct = default);

    /// <summary>
    /// Builds the Redis HASH key for a document identifier.
    /// </summary>
    string GetDocumentKey(string documentId);
}
