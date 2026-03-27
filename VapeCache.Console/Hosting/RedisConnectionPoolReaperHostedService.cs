using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Console.Stress;

namespace VapeCache.Console.Hosting;

internal sealed class RedisConnectionPoolReaperHostedService : BackgroundService, IHostedLifecycleService
{
    private readonly IOptions<RedisStressOptions> stressOptions;
    private readonly IRedisConnectionPoolReaper reaper;
    private readonly ILogger<RedisConnectionPoolReaperHostedService> logger;

    public RedisConnectionPoolReaperHostedService(
        IOptions<RedisStressOptions> stressOptions,
        IRedisConnectionPoolReaper reaper,
        ILogger<RedisConnectionPoolReaperHostedService> logger)
    {
        this.stressOptions = stressOptions;
        this.reaper = reaper;
        this.logger = logger;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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
