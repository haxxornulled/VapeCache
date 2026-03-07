using Microsoft.Extensions.Logging;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Modules;

namespace VapeCache.Infrastructure.Modules;

internal sealed partial class RedisBloomService : IRedisBloomService, IDisposable
{
    private readonly IRedisCommandExecutor _redis;
    private readonly IRedisFallbackCommandExecutor _fallback;
    private readonly IRedisModuleDetector _modules;
    private readonly ILogger<RedisBloomService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool? _available;

    public RedisBloomService(
        IRedisCommandExecutor redis,
        IRedisFallbackCommandExecutor fallback,
        IRedisModuleDetector modules,
        ILogger<RedisBloomService> logger)
    {
        _redis = redis;
        _fallback = fallback;
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

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_available == true)
                return true;

            var available = await _modules.IsModuleInstalledAsync("bf", ct).ConfigureAwait(false);
            if (available)
                _available = true;
            return available;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Adds value.
    /// </summary>
    public async ValueTask<bool> AddAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct = default)
    {
        if (await IsAvailableAsync(ct).ConfigureAwait(false))
            return await _redis.BfAddAsync(key, item, ct).ConfigureAwait(false);

        LogFallbackToMemory(_logger, key);
        return await _fallback.BfAddAsync(key, item, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> ExistsAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct = default)
    {
        if (await IsAvailableAsync(ct).ConfigureAwait(false))
            return await _redis.BfExistsAsync(key, item, ct).ConfigureAwait(false);

        return await _fallback.BfExistsAsync(key, item, ct).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 23000,
        Level = LogLevel.Debug,
        Message = "RedisBloom unavailable; using in-memory fallback for {Key}.")]
    private static partial void LogFallbackToMemory(ILogger logger, string key);

    public void Dispose() => _gate.Dispose();
}
