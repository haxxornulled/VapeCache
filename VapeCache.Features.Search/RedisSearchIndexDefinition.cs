using VapeCache.Abstractions.Modules;

namespace VapeCache.Features.Search;

/// <summary>
/// Immutable definition for a HASH-backed RediSearch index.
/// </summary>
public sealed class RedisSearchIndexDefinition
{
    /// <summary>
    /// Creates an index definition.
    /// </summary>
    public RedisSearchIndexDefinition(
        string indexName,
        string documentKeyPrefix,
        IEnumerable<RedisSearchFieldDefinition> fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKeyPrefix);
        ArgumentNullException.ThrowIfNull(fields);

        var normalizedFields = fields.ToArray();
        if (normalizedFields.Length == 0)
            throw new ArgumentException("At least one search field is required.", nameof(fields));

        IndexName = indexName;
        DocumentKeyPrefix = documentKeyPrefix;
        Fields = normalizedFields;
    }

    /// <summary>
    /// Index name.
    /// </summary>
    public string IndexName { get; }

    /// <summary>
    /// Prefix used for HASH documents attached to the index.
    /// </summary>
    public string DocumentKeyPrefix { get; }

    /// <summary>
    /// Schema fields.
    /// </summary>
    public IReadOnlyList<RedisSearchFieldDefinition> Fields { get; }

    /// <summary>
    /// Builds a document key for the given identifier.
    /// </summary>
    public string GetDocumentKey(string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        return string.Concat(DocumentKeyPrefix, documentId);
    }
}
