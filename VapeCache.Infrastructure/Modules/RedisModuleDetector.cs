using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Modules;

namespace VapeCache.Infrastructure.Modules;

/// <summary>
/// Detects installed Redis modules by querying MODULE LIST.
/// Results are cached to avoid repeated network calls.
/// </summary>
internal sealed class RedisModuleDetector : IRedisModuleDetector
{
    private static readonly TimeSpan FailureRetryBackoff = TimeSpan.FromSeconds(5);

    private readonly IRedisCommandExecutor _executor;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _failureRetryBackoff;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string[]? _cachedModules;
    private bool _modulesCached;
    private long _retryAfterUtcTicks;

    public RedisModuleDetector(IRedisCommandExecutor executor)
        : this(executor, TimeProvider.System, FailureRetryBackoff)
    {
    }

    internal RedisModuleDetector(IRedisCommandExecutor executor, TimeProvider timeProvider, TimeSpan failureRetryBackoff)
    {
        _executor = executor;
        _timeProvider = timeProvider;
        _failureRetryBackoff = failureRetryBackoff < TimeSpan.Zero ? TimeSpan.Zero : failureRetryBackoff;
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
        if (Volatile.Read(ref _modulesCached) && _cachedModules is not null)
            return _cachedModules;

        var retryAfterUtcTicks = Interlocked.Read(ref _retryAfterUtcTicks);
        if (retryAfterUtcTicks != 0 && _timeProvider.GetUtcNow().Ticks < retryAfterUtcTicks)
            return Array.Empty<string>();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _modulesCached) && _cachedModules is not null)
                return _cachedModules;

            retryAfterUtcTicks = Interlocked.Read(ref _retryAfterUtcTicks);
            if (retryAfterUtcTicks != 0 && _timeProvider.GetUtcNow().Ticks < retryAfterUtcTicks)
                return Array.Empty<string>();

            try
            {
                var modules = await _executor.ModuleListAsync(ct).ConfigureAwait(false);
                _cachedModules = modules;
                Volatile.Write(ref _modulesCached, true);
                Interlocked.Exchange(ref _retryAfterUtcTicks, 0);
                return modules;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                if (_failureRetryBackoff > TimeSpan.Zero)
                {
                    var untilTicks = _timeProvider.GetUtcNow().Add(_failureRetryBackoff).Ticks;
                    Interlocked.Exchange(ref _retryAfterUtcTicks, untilTicks);
                }

                return Array.Empty<string>();
            }
        }
        finally
        {
            _gate.Release();
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
