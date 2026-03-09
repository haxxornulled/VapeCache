using System;
using System.Buffers;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using VapeCache.Abstractions.Caching;

namespace ResultDemo.Examples;

public sealed class HybridCacheServiceExamples(
    ICacheService cache,
    IRedisCircuitBreakerState breaker,
    IRedisFailoverController failover,
    ILogger<HybridCacheServiceExamples> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim FactoryGate = new(1, 1);
    private static readonly object FactoryLock = new();
    private static int _factoryCalls;

    public async ValueTask RunAsync(CancellationToken ct)
    {
        logger.LogInformation("Hybrid cache name: {Name}", cache.Name);
        LogBreakerState("initial");

        var key = "profile:alice";
        var payload = new CacheEntryOptions(TimeSpan.FromMinutes(5));

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Profile("Alice", 7), JsonOptions);
        await cache.SetAsync(key, bytes, payload, ct);

        var raw = await cache.GetAsync(key, ct);
        logger.LogInformation("GetAsync bytes length: {Length}", raw?.Length ?? 0);

        var removed = await cache.RemoveAsync(key, ct);
        logger.LogInformation("RemoveAsync removed: {Removed}", removed);

        await cache.SetAsync(
            key,
            new Profile("Alice", 8),
            SerializeProfile,
            payload,
            ct);

        var typed = await cache.GetAsync(key, DeserializeProfile, ct);
        logger.LogInformation("GetAsync<T> profile: {Name}/{Level}", typed?.Name, typed?.Level);

        var cached = await cache.GetOrSetAsync(
            "profile:cached",
            async token =>
            {
                await FactoryGate.WaitAsync(token);
                try
                {
                    lock (FactoryLock)
                        _factoryCalls++;
                }
                finally
                {
                    FactoryGate.Release();
                }

                return new Profile("Cached", _factoryCalls);
            },
            SerializeProfile,
            DeserializeProfile,
            payload,
            ct);
        logger.LogInformation("GetOrSetAsync profile: {Name}/{Level}", cached.Name, cached.Level);

        failover.MarkRedisFailure();
        LogBreakerState("after failure");

        failover.MarkRedisSuccess();
        LogBreakerState("after success");

        failover.ForceOpen("maintenance");
        LogBreakerState("forced open");

        failover.ClearForcedOpen();
        LogBreakerState("after clear");
    }

    private void LogBreakerState(string label)
    {
        logger.LogInformation(
            "Breaker {Label}: enabled={Enabled} open={Open} failures={Failures} remaining={Remaining} halfOpen={HalfOpen}",
            label,
            breaker.Enabled,
            breaker.IsOpen,
            breaker.ConsecutiveFailures,
            breaker.OpenRemaining,
            breaker.HalfOpenProbeInFlight);
    }

    private static void SerializeProfile(IBufferWriter<byte> writer, Profile value)
    {
        using var jsonWriter = new Utf8JsonWriter(writer);
        JsonSerializer.Serialize(jsonWriter, value, JsonOptions);
    }

    private static Profile DeserializeProfile(ReadOnlySpan<byte> data)
        => JsonSerializer.Deserialize<Profile>(data, JsonOptions) ?? new Profile("unknown", 0);

    private sealed record Profile(string Name, int Level);
}
