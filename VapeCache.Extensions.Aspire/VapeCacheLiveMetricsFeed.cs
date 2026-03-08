using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Diagnostics;

namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Defines the vape cache live metrics feed contract.
/// </summary>
public interface IVapeCacheLiveMetricsFeed
{
    /// <summary>
    /// Executes subscribe.
    /// </summary>
    ChannelReader<VapeCacheLiveSample> Subscribe(CancellationToken ct);
}

/// <summary>
/// Represents the vape cache live sample.
/// </summary>
/// <param name="TimestampUtc">Sample timestamp in UTC.</param>
/// <param name="CurrentBackend">Backend active when the sample was captured.</param>
/// <param name="Hits">Total cache hits.</param>
/// <param name="Misses">Total cache misses.</param>
/// <param name="SetCalls">Total set operations.</param>
/// <param name="RemoveCalls">Total remove operations.</param>
/// <param name="FallbackToMemory">Total requests served by in-memory fallback.</param>
/// <param name="RedisBreakerOpened">Total breaker-open events.</param>
/// <param name="StampedeKeyRejected">Total requests rejected by stampede key gating.</param>
/// <param name="StampedeLockWaitTimeout">Total requests timing out while waiting on stampede locks.</param>
/// <param name="StampedeFailureBackoffRejected">Total requests rejected by stampede failure backoff.</param>
/// <param name="HitRate">Computed cache hit rate for the sample window.</param>
/// <param name="Spill">Optional spill-store diagnostics snapshot.</param>
/// <param name="Autoscaler">Optional redis autoscaler snapshot.</param>
/// <param name="Lanes">Optional lane-level mux diagnostics snapshots.</param>
public sealed record VapeCacheLiveSample(
    DateTimeOffset TimestampUtc,
    [property: JsonConverter(typeof(JsonStringEnumConverter<BackendType>))] BackendType CurrentBackend,
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
    SpillStoreDiagnosticsSnapshot? Spill,
    RedisAutoscalerSnapshot? Autoscaler,
    IReadOnlyList<RedisMuxLaneSnapshot>? Lanes = null);

internal sealed class VapeCacheLiveMetricsFeed(
    ICacheStats stats,
    IRedisCircuitBreakerState? breakerState,
    IRedisFailoverController? failoverController,
    IOptions<VapeCacheEndpointOptions> options,
    ISpillStoreDiagnostics? spillDiagnostics = null,
    IRedisMultiplexerDiagnostics? diagnostics = null) : BackgroundService, IVapeCacheLiveMetricsFeed
{
    private readonly ConcurrentDictionary<int, Subscriber> _subscribers = new();
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
        var subscriber = new Subscriber(channel);
        _subscribers[id] = subscriber;
        subscriber.CancellationRegistration = ct.Register(
            static state =>
            {
                var callbackState = (SubscriberCancellationState)state!;
                callbackState.Owner.RemoveSubscriber(callbackState.Id);
            },
            new SubscriberCancellationState(this, id));

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
                CurrentBackend: ResolveDashboardBackend(
                    breakerState?.IsOpen ?? false,
                    failoverController?.IsForcedOpen ?? false),
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
                Spill: spillDiagnostics?.GetSnapshot(),
                Autoscaler: diagnostics?.GetAutoscalerSnapshot(),
                Lanes: diagnostics?.GetMuxLaneSnapshots());

            foreach (var kvp in _subscribers)
            {
                if (!kvp.Value.Channel.Writer.TryWrite(sample))
                    continue;
            }
        }

        foreach (var subscriber in _subscribers.Values)
            subscriber.CompleteAndDispose();
        _subscribers.Clear();
    }

    public override void Dispose()
    {
        foreach (var subscriber in _subscribers.Values)
            subscriber.CompleteAndDispose();
        _subscribers.Clear();
        base.Dispose();
    }

    private void RemoveSubscriber(int id)
    {
        if (_subscribers.TryRemove(id, out var subscriber))
            subscriber.CompleteAndDispose();
    }

    private sealed class Subscriber(Channel<VapeCacheLiveSample> channel)
    {
        public Channel<VapeCacheLiveSample> Channel { get; } = channel;
        public CancellationTokenRegistration CancellationRegistration { get; set; }

        public void CompleteAndDispose()
        {
            Channel.Writer.TryComplete();
            CancellationRegistration.Dispose();
        }
    }

    private sealed record SubscriberCancellationState(VapeCacheLiveMetricsFeed Owner, int Id);

    private static BackendType ResolveDashboardBackend(bool breakerOpen, bool forcedOpen)
        => (forcedOpen || breakerOpen) ? BackendType.InMemory : BackendType.Redis;
}
