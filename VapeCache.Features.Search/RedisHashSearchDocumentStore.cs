using System.Text;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Modules;

namespace VapeCache.Features.Search;

/// <summary>
/// Generic HASH-backed search projection store for RediSearch workloads.
/// </summary>
public sealed class RedisHashSearchDocumentStore<TDocument> : IRedisHashSearchDocumentStore<TDocument>, IDisposable
{
    private readonly IRedisCommandExecutor _redis;
    private readonly IRedisSearchService _search;
    private readonly IRedisHashSearchDocumentMapper<TDocument> _mapper;
    private readonly IOptionsMonitor<VapeCacheSearchOptions> _optionsMonitor;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private int _indexReady;

    /// <summary>
    /// Creates a projection store.
    /// </summary>
    public RedisHashSearchDocumentStore(
        IRedisCommandExecutor redis,
        IRedisSearchService search,
        IRedisHashSearchDocumentMapper<TDocument> mapper,
        IOptionsMonitor<VapeCacheSearchOptions> optionsMonitor)
    {
        _redis = redis;
        _search = search;
        _mapper = mapper;
        _optionsMonitor = optionsMonitor;
    }

    /// <summary>
    /// Index definition for the mapped type.
    /// </summary>
    public RedisSearchIndexDefinition Index => _mapper.Index;

    /// <summary>
    /// Ensures the RediSearch index exists.
    /// </summary>
    public async ValueTask<bool> EnsureIndexAsync(CancellationToken ct = default)
    {
        if (!_optionsMonitor.CurrentValue.Enabled)
            return false;

        if (Volatile.Read(ref _indexReady) == 1)
            return true;

        var available = await _search.IsAvailableAsync(ct).ConfigureAwait(false);
        if (!available)
        {
            if (_optionsMonitor.CurrentValue.RequireModuleAvailability)
            {
                throw new InvalidOperationException(
                    $"RediSearch is required for index '{Index.IndexName}' but is not available on the active Redis server.");
            }

            return false;
        }

        await _indexGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _indexReady) == 1)
                return true;

            _ = await _search.CreateIndexAsync(Index.IndexName, Index.DocumentKeyPrefix, Index.Fields, ct).ConfigureAwait(false);
            Volatile.Write(ref _indexReady, 1);
            return true;
        }
        finally
        {
            _indexGate.Release();
        }
    }

    /// <summary>
    /// Upserts a HASH-backed search projection.
    /// </summary>
    public async ValueTask<string> UpsertAsync(TDocument document, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var documentId = _mapper.GetDocumentId(document);
        var key = GetDocumentKey(documentId);
        var mappedFields = _mapper.MapFields(document);
        if (mappedFields.Count == 0)
            throw new InvalidOperationException($"Search document '{typeof(TDocument).Name}' produced no HASH fields.");

        var writeFields = new List<(string Field, ReadOnlyMemory<byte> Value)>(mappedFields.Count);
        for (var i = 0; i < mappedFields.Count; i++)
        {
            var field = mappedFields[i];
            if (string.IsNullOrWhiteSpace(field.Field) || field.Value is null)
                continue;

            writeFields.Add((field.Field, Encoding.UTF8.GetBytes(field.Value)));
        }

        if (writeFields.Count == 0)
            throw new InvalidOperationException($"Search document '{typeof(TDocument).Name}' produced no materialized HASH values.");

        _ = await _redis.HSetManyAsync(key, [.. writeFields], ct).ConfigureAwait(false);
        if (ttl.HasValue)
            _ = await _redis.ExpireAsync(key, ttl.Value, ct).ConfigureAwait(false);

        return key;
    }

    /// <summary>
    /// Deletes a search projection document.
    /// </summary>
    public ValueTask<bool> DeleteAsync(string documentId, CancellationToken ct = default)
        => _redis.DeleteAsync(GetDocumentKey(documentId), ct);

    /// <summary>
    /// Searches the index and returns matching document ids.
    /// </summary>
    public async ValueTask<string[]> SearchIdsAsync(RedisSearchQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!await EnsureIndexAsync(ct).ConfigureAwait(false))
            return Array.Empty<string>();

        var effectiveQuery = ApplyDefaults(query);
        return await _search.SearchAsync(
            Index.IndexName,
            effectiveQuery.RawQuery,
            effectiveQuery.Offset,
            effectiveQuery.Count,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Counts index hits for a query.
    /// </summary>
    public async ValueTask<long> SearchCountAsync(RedisSearchQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!await EnsureIndexAsync(ct).ConfigureAwait(false))
            return 0L;

        var effectiveQuery = ApplyDefaults(query);
        return await _search.SearchCountAsync(
            Index.IndexName,
            effectiveQuery.RawQuery,
            effectiveQuery.Offset,
            effectiveQuery.Count,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the HASH key for a document id.
    /// </summary>
    public string GetDocumentKey(string documentId)
        => Index.GetDocumentKey(documentId);

    /// <inheritdoc />
    public void Dispose() => _indexGate.Dispose();

    private RedisSearchQuery ApplyDefaults(RedisSearchQuery query)
    {
        var options = _optionsMonitor.CurrentValue;
        if (query.Count.HasValue || options.DefaultResultCount <= 0)
            return query;

        return new RedisSearchQuery(query.RawQuery, query.Offset, options.DefaultResultCount);
    }
}
