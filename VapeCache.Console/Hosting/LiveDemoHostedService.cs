using System.Buffers;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Diagnostics;

namespace VapeCache.Console.Hosting;

internal sealed class LiveDemoHostedService(
    IOptions<LiveDemoOptions> demoOptions,
    ICacheService cache,
    ICacheBackendState backendState,
    IRedisCircuitBreakerState? circuitBreaker,
    ILogger<LiveDemoHostedService> logger) : BackgroundService, IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var demo = demoOptions.Value;
        if (!demo.Enabled) return;

        logger.LogInformation("Live demo enabled.");
        logger.LogInformation("Demo: Key={Key} Ttl={Ttl} Interval={Interval}", demo.Key, demo.Ttl, demo.Interval);

        static void Serialize(IBufferWriter<byte> w, string v)
        {
            var bytes = Encoding.UTF8.GetBytes(v);
            var span = w.GetSpan(bytes.Length);
            bytes.CopyTo(span);
            w.Advance(bytes.Length);
        }

        static string Deserialize(ReadOnlySpan<byte> s) => Encoding.UTF8.GetString(s);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var value = await cache.GetOrSetAsync(
                        demo.Key,
                        _ => ValueTask.FromResult(DateTimeOffset.UtcNow.ToString("O")),
                        Serialize,
                        Deserialize,
                        new CacheEntryOptions(demo.Ttl),
                        stoppingToken)
                    .ConfigureAwait(false);

                logger.LogInformation(
                    "Live demo tick: Value={Value} Backend={Backend} BreakerOpen={BreakerOpen} Failures={Failures} RemainingMs={RemainingMs}",
                    value,
                    backendState.EffectiveBackend.ToWireName(),
                    circuitBreaker?.IsOpen,
                    circuitBreaker?.ConsecutiveFailures,
                    circuitBreaker?.OpenRemaining?.TotalMilliseconds);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Live demo tick failed.");
            }

            try
            {
                await Task.Delay(demo.Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
