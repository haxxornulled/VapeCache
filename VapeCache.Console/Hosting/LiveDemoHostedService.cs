using System.Buffers;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Console.Hosting;

internal sealed class LiveDemoHostedService(
    IOptions<WebHostOptions> webOptions,
    IOptions<LiveDemoOptions> demoOptions,
    ICacheService cache,
    ICurrentCacheService current,
    IServiceProvider services,
    ILogger<LiveDemoHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var web = webOptions.Value;
        var demo = demoOptions.Value;
        if (!demo.Enabled) return;

        logger.LogInformation("Live demo enabled. Web={WebEnabled} Urls={Urls}", web.Enabled, web.Urls);
        logger.LogInformation("Endpoints: GET /healthz | GET /cache/current | GET /cache/breaker | GET /cache/stats | PUT/GET/DELETE /cache/{{key}} | POST /cache/{{key}}/get-or-set");
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

                var breaker = services.GetService(typeof(IRedisCircuitBreakerState)) as IRedisCircuitBreakerState;

                logger.LogInformation(
                    "Live demo tick: Value={Value} Backend={Backend} BreakerOpen={BreakerOpen} Failures={Failures} RemainingMs={RemainingMs}",
                    value,
                    current.CurrentName,
                    breaker?.IsOpen,
                    breaker?.ConsecutiveFailures,
                    breaker?.OpenRemaining?.TotalMilliseconds);
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
