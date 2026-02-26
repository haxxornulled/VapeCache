using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Console.Stress;

namespace VapeCache.Console.Hosting;

internal sealed class RedisConnectionPoolReaperHostedService(
    IOptions<RedisStressOptions> stressOptions,
    IRedisConnectionPoolReaper reaper,
    ILogger<RedisConnectionPoolReaperHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mode = (stressOptions.Value.Mode ?? "pool").Trim().ToLowerInvariant();
        if (mode != "pool")
            return;

        logger.LogInformation("Redis pool reaper hosted service starting.");
        try
        {
            await reaper.RunReaperAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            logger.LogInformation("Redis pool reaper hosted service stopping.");
        }
    }
}
