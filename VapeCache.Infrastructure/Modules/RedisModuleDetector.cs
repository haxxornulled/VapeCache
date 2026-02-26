using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Modules;

namespace VapeCache.Infrastructure.Modules;

/// <summary>
/// Detects installed Redis modules by querying MODULE LIST.
/// Results are cached to avoid repeated network calls.
/// </summary>
internal sealed class RedisModuleDetector : IRedisModuleDetector
{
    private readonly IRedisCommandExecutor _executor;
    private string[]? _cachedModules;
    private bool _modulesCached;

    public RedisModuleDetector(IRedisCommandExecutor executor)
    {
        _executor = executor;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> IsModuleInstalledAsync(string moduleName, CancellationToken ct = default)
    {
        var modules = await GetInstalledModulesAsync(ct).ConfigureAwait(false);
        return Array.Exists(modules, m => string.Equals(m, moduleName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<string[]> GetInstalledModulesAsync(CancellationToken ct = default)
    {
        // Return cached result if available
        if (_modulesCached && _cachedModules is not null)
            return _cachedModules;

        try
        {
            var modules = await _executor.ModuleListAsync(ct).ConfigureAwait(false);
            _cachedModules = modules;
            _modulesCached = true;
            return modules;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // If MODULE LIST fails (old Redis or no modules), return empty
            _cachedModules = Array.Empty<string>();
            _modulesCached = true;
            return _cachedModules;
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> HasRedisJsonAsync(CancellationToken ct = default)
    {
        // RedisJSON module is named "ReJSON"
        return await IsModuleInstalledAsync("ReJSON", ct).ConfigureAwait(false);
    }
}
