using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Extensions.Aspire;

public interface IVapeCacheLiveMetricsFeed
{
    ChannelReader<VapeCacheLiveSample> Subscribe(CancellationToken ct);
}

public sealed record VapeCacheLiveSample(
    DateTimeOffset TimestampUtc,
    string CurrentBackend,
    long Hits,
    long Misses,
    long SetCalls,
    long RemoveCalls,
    long FallbackToMemory,
    long RedisBreakerOpened,
    long StampedeKeyRejected,
    long StampedeLockWaitTimeout,
    long StampedeFailureBackoffRejected,
    double HitRate,
    RedisAutoscalerSnapshot? Autoscaler);

internal sealed class VapeCacheLiveMetricsFeed(
    ICacheStats stats,
    ICurrentCacheService current,
    IOptions<VapeCacheEndpointOptions> options,
    IRedisMultiplexerDiagnostics? diagnostics = null) : BackgroundService, IVapeCacheLiveMetricsFeed
{
    private readonly ConcurrentDictionary<int, Channel<VapeCacheLiveSample>> _subscribers = new();
    private int _subscriberId;

    /// <summary>
    /// Executes value.
    /// </summary>
    public ChannelReader<VapeCacheLiveSample> Subscribe(CancellationToken ct)
    {
        var capacity = Math.Max(8, options.Value.LiveChannelCapacity);
        var channel = Channel.CreateBounded<VapeCacheLiveSample>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

        var id = Interlocked.Increment(ref _subscriberId);
        _subscribers[id] = channel;

        ct.Register(() =>
        {
            if (_subscribers.TryRemove(id, out var existing))
                existing.Writer.TryComplete();
        });

        return channel.Reader;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.LiveSampleInterval <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(1)
            : options.Value.LiveSampleInterval;

        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested &&
               await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (_subscribers.IsEmpty)
                continue;

            var snapshot = stats.Snapshot;
            var reads = snapshot.Hits + snapshot.Misses;
            var sample = new VapeCacheLiveSample(
                TimestampUtc: DateTimeOffset.UtcNow,
                CurrentBackend: current.CurrentName,
                Hits: snapshot.Hits,
                Misses: snapshot.Misses,
                SetCalls: snapshot.SetCalls,
                RemoveCalls: snapshot.RemoveCalls,
                FallbackToMemory: snapshot.FallbackToMemory,
                RedisBreakerOpened: snapshot.RedisBreakerOpened,
                StampedeKeyRejected: snapshot.StampedeKeyRejected,
                StampedeLockWaitTimeout: snapshot.StampedeLockWaitTimeout,
                StampedeFailureBackoffRejected: snapshot.StampedeFailureBackoffRejected,
                HitRate: reads == 0 ? 0d : (double)snapshot.Hits / reads,
                Autoscaler: diagnostics?.GetAutoscalerSnapshot());

            foreach (var kvp in _subscribers)
            {
                if (!kvp.Value.Writer.TryWrite(sample))
                    continue;
            }
        }

        foreach (var channel in _subscribers.Values)
            channel.Writer.TryComplete();
        _subscribers.Clear();
    }
}
