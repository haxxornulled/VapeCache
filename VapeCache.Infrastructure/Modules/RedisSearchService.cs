using System.Linq;
using Microsoft.Extensions.Logging;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Modules;

namespace VapeCache.Infrastructure.Modules;

internal sealed partial class RedisSearchService : IRedisSearchService, IDisposable
{
    private readonly IRedisCommandExecutor _redis;
    private readonly IRedisModuleDetector _modules;
    private readonly ILogger<RedisSearchService> _logger;
    private bool? _available;

    public RedisSearchService(IRedisCommandExecutor redis, IRedisModuleDetector modules, ILogger<RedisSearchService> logger)
    {
        _redis = redis;
        _modules = modules;
        _logger = logger;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_available == true)
            return true;

        var modules = await _modules.GetInstalledModulesAsync(ct).ConfigureAwait(false);
        var available = modules.Any(m =>
            string.Equals(m, "search", StringComparison.OrdinalIgnoreCase)
            || string.Equals(m, "ft", StringComparison.OrdinalIgnoreCase));
        if (available)
            _available = true;
        return available;
    }

    /// <summary>
    /// Creates value.
    /// </summary>
    public ValueTask<bool> CreateIndexAsync(string index, string prefix, string[] fields, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fields);

        RedisSearchFieldDefinition[] schema = new RedisSearchFieldDefinition[fields.Length];
        for (var i = 0; i < fields.Length; i++)
            schema[i] = RedisSearchFieldDefinition.Text(fields[i]);

        return CreateIndexAsync(index, prefix, schema, ct);
    }

    /// <summary>
    /// Creates value.
    /// </summary>
    public async ValueTask<bool> CreateIndexAsync(
        string index,
        string prefix,
        IReadOnlyList<RedisSearchFieldDefinition> fields,
        CancellationToken ct = default)
    {
        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
        {
            LogRedisSearchUnavailable(_logger, index);
            return false;
        }

        return await _redis.FtCreateAsync(index, prefix, fields, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<string[]> SearchAsync(string index, string query, int? offset = null, int? count = null, CancellationToken ct = default)
    {
        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
            return Array.Empty<string>();

        return await _redis.FtSearchAsync(index, query, offset, count, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> SearchCountAsync(
        string index,
        string query,
        int? offset = null,
        int? count = null,
        CancellationToken ct = default)
    {
        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
            return 0L;

        return await _redis.FtSearchCountAsync(index, query, offset, count, ct).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 7301,
        Level = LogLevel.Warning,
        Message = "RediSearch module not available; FT.CREATE for {Index} ignored.")]
    private static partial void LogRedisSearchUnavailable(ILogger logger, string index);

    public void Dispose()
    {
    }
}
