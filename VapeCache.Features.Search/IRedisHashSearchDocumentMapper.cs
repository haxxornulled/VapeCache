namespace VapeCache.Features.Search;

/// <summary>
/// Maps an application document to a HASH-backed RediSearch projection.
/// </summary>
public interface IRedisHashSearchDocumentMapper<TDocument>
{
    /// <summary>
    /// Index definition for the mapped document type.
    /// </summary>
    RedisSearchIndexDefinition Index { get; }

    /// <summary>
    /// Gets the stable document identifier.
    /// </summary>
    string GetDocumentId(TDocument document);

    /// <summary>
    /// Maps the document to HASH fields.
    /// </summary>
    IReadOnlyList<RedisSearchHashFieldValue> MapFields(TDocument document);
}
