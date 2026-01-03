namespace VapeCache.Abstractions.Modules;

/// <summary>
/// RediSearch integration for indexing and querying cached data.
/// </summary>
public interface IRedisSearchService
{
    ValueTask<bool> IsAvailableAsync(CancellationToken ct = default);
    ValueTask<bool> CreateIndexAsync(string index, string prefix, string[] fields, CancellationToken ct = default);
    ValueTask<string[]> SearchAsync(string index, string query, int? offset = null, int? count = null, CancellationToken ct = default);
}
