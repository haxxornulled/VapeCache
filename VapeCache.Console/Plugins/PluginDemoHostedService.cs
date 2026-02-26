using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Console.Plugins;

internal sealed class PluginDemoHostedService(
    IEnumerable<IVapeCachePlugin> plugins,
    IOptionsMonitor<PluginDemoOptions> optionsMonitor,
    ICacheService cache,
    ICurrentCacheService current,
    ILogger<PluginDemoHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = optionsMonitor.CurrentValue;
        if (!options.Enabled)
        {
            logger.LogDebug("Plugin demo is disabled.");
            return;
        }

        var pluginList = plugins as IVapeCachePlugin[] ?? plugins.ToArray();
        if (pluginList.Length == 0)
        {
            logger.LogInformation("Plugin demo enabled, but no plugins are registered.");
            return;
        }

        logger.LogInformation("Plugin demo starting with {Count} plugin(s).", pluginList.Length);

        foreach (var plugin in pluginList)
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            try
            {
                await plugin.ExecuteAsync(cache, current, stoppingToken).ConfigureAwait(false);
                logger.LogInformation("Plugin {PluginName} completed.", plugin.Name);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Plugin {PluginName} failed.", plugin.Name);
            }
        }

        logger.LogInformation("Plugin demo finished.");
    }
}
