using System.Linq;
using Microsoft.Extensions.Logging;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Modules;

namespace VapeCache.Infrastructure.Modules;

internal sealed class RedisSearchService : IRedisSearchService
{
    private readonly IRedisCommandExecutor _redis;
    private readonly IRedisModuleDetector _modules;
    private readonly ILogger<RedisSearchService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
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
        if (_available.HasValue)
            return _available.Value;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_available.HasValue)
                return _available.Value;

            var modules = await _modules.GetInstalledModulesAsync(ct).ConfigureAwait(false);
            var available = modules.Any(m =>
                string.Equals(m, "search", StringComparison.OrdinalIgnoreCase)
                || string.Equals(m, "ft", StringComparison.OrdinalIgnoreCase));
            _available = available;
            return available;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Creates value.
    /// </summary>
    public async ValueTask<bool> CreateIndexAsync(string index, string prefix, string[] fields, CancellationToken ct = default)
    {
        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
        {
            _logger.LogWarning("RediSearch module not available; FT.CREATE for {Index} ignored.", index);
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
}
