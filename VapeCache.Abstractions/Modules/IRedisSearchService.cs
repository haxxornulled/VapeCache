namespace VapeCache.Abstractions.Modules;

/// <summary>
/// RediSearch integration for indexing and querying cached data.
/// </summary>
public interface IRedisSearchService
{
    /// <summary>
    /// Executes s available async.
    /// </summary>
    ValueTask<bool> IsAvailableAsync(CancellationToken ct = default);
    /// <summary>
    /// Executes create index async.
    /// </summary>
    ValueTask<bool> CreateIndexAsync(string index, string prefix, string[] fields, CancellationToken ct = default);
    /// <summary>
    /// Executes search async.
    /// </summary>
    ValueTask<string[]> SearchAsync(string index, string query, int? offset = null, int? count = null, CancellationToken ct = default);
}
