using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Console.Plugins;

internal sealed class SampleCatalogPlugin(
    IOptionsMonitor<PluginDemoOptions> optionsMonitor,
    ILogger<SampleCatalogPlugin> logger) : IVapeCachePlugin
{
    public string Name => "sample-catalog";

    public async ValueTask ExecuteAsync(
        ICacheService cache,
        ICurrentCacheService current,
        CancellationToken cancellationToken)
    {
        var options = optionsMonitor.CurrentValue;
        var key = $"{options.KeyPrefix}:catalog:last-sync";
        var payload = Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O"));

        await cache.SetAsync(key, payload, new CacheEntryOptions(options.Ttl), cancellationToken).ConfigureAwait(false);
        var roundTrip = await cache.GetAsync(key, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Plugin {PluginName} executed. Key={Key} Bytes={Bytes} Backend={Backend}",
            Name,
            key,
            roundTrip?.Length ?? 0,
            current.CurrentName);
    }
}
