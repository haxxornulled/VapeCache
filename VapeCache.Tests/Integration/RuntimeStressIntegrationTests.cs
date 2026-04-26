using System.Globalization;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Diagnostics;
using VapeCache.Extensions.DependencyInjection;
using VapeCache.Extensions.DistributedCache;
using Xunit.Sdk;

namespace VapeCache.Tests.Integration;

[Collection(RedisIntegrationCollection.Name)]
public sealed class RuntimeStressIntegrationTests
{
    [SkippableFact]
    public async Task MixedNativeAndDistributedTraffic_TracksOriginStatsUnderLoad()
    {
        Skip.IfNot(IsStressEnabled(), "Set VAPECACHE_RUNTIME_STRESS_ENABLED=true to run runtime stress integration.");

        var redis = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(redis is null, skipReason);

        var workers = Math.Max(4, TryGetInt("VAPECACHE_RUNTIME_STRESS_WORKERS") ?? 24);
        var keyspace = Math.Max(16, TryGetInt("VAPECACHE_RUNTIME_STRESS_KEYSPACE") ?? 256);
        var durationSeconds = Math.Max(5, TryGetInt("VAPECACHE_RUNTIME_STRESS_DURATION_SECONDS") ?? 10);

        await using var provider = BuildRuntimeProvider(redis);
        var cache = provider.GetRequiredService<IVapeCache>();
        var distributed = provider.GetRequiredService<IDistributedCache>();
        var backendState = provider.GetRequiredService<ICacheBackendState>();
        var cacheStats = provider.GetRequiredService<ICacheStats>();
        var originStats = provider.GetRequiredService<ICacheOriginStats>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds + 30));
        var result = await RunMixedTrafficAsync(
            cache,
            distributed,
            "mixed",
            workers,
            keyspace,
            TimeSpan.FromSeconds(durationSeconds),
            cts.Token).ConfigureAwait(false);

        var cacheSnapshot = cacheStats.Snapshot;
        var originSnapshot = originStats.Snapshot;

        Assert.True(result.TotalOperations > 0, "Expected stress workload to execute operations.");
        Assert.True(
            result.TotalFailures < Math.Max(4, result.TotalOperations / 20),
            $"Mixed traffic degraded too far: ops={result.TotalOperations} fail={result.TotalFailures}");
        Assert.True(cacheSnapshot.GetCalls > 0, "Expected cache get calls to be recorded.");
        Assert.True(cacheSnapshot.SetCalls > 0, "Expected cache set calls to be recorded.");
        Assert.True(originSnapshot.NativeReads > 0, "Expected native reads to be recorded.");
        Assert.True(originSnapshot.NativeWrites > 0, "Expected native writes to be recorded.");
        Assert.True(originSnapshot.InteropReads > 0, "Expected distributed-cache reads to be recorded.");
        Assert.True(originSnapshot.InteropWrites > 0, "Expected distributed-cache writes to be recorded.");
        Assert.Equal(BackendType.Redis, backendState.EffectiveBackend);
    }

    [SkippableFact]
    public async Task ForcedFailover_DuringMixedTraffic_PreservesAvailability_AndReflectsFallback()
    {
        Skip.IfNot(IsStressEnabled(), "Set VAPECACHE_RUNTIME_STRESS_ENABLED=true to run runtime stress integration.");

        var redis = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(redis is null, skipReason);

        var workers = Math.Max(4, TryGetInt("VAPECACHE_RUNTIME_STRESS_WORKERS") ?? 24);
        var keyspace = Math.Max(16, TryGetInt("VAPECACHE_RUNTIME_STRESS_KEYSPACE") ?? 256);
        var durationSeconds = Math.Max(6, TryGetInt("VAPECACHE_RUNTIME_STRESS_DURATION_SECONDS") ?? 10);
        var forceOpenAfterMs = Math.Max(250, TryGetInt("VAPECACHE_RUNTIME_STRESS_FORCE_OPEN_AFTER_MS") ?? 1500);
        var forceOpenHoldMs = Math.Max(500, TryGetInt("VAPECACHE_RUNTIME_STRESS_FORCE_OPEN_HOLD_MS") ?? 2500);

        await using var provider = BuildRuntimeProvider(redis);
        var cache = provider.GetRequiredService<IVapeCache>();
        var distributed = provider.GetRequiredService<IDistributedCache>();
        var backendState = provider.GetRequiredService<ICacheBackendState>();
        var cacheStats = provider.GetRequiredService<ICacheStats>();
        var originStats = provider.GetRequiredService<ICacheOriginStats>();
        var failover = provider.GetRequiredService<IRedisFailoverController>();

        var observedBackends = new List<BackendType>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds + 30));
        using var samplerCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var samplerTask = Task.Run(async () =>
        {
            while (!samplerCts.IsCancellationRequested)
            {
                observedBackends.Add(backendState.EffectiveBackend);
                try
                {
                    await Task.Delay(100, samplerCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (samplerCts.IsCancellationRequested)
                {
                    break;
                }
            }
        }, samplerCts.Token);

        var forceTask = Task.Run(async () =>
        {
            await Task.Delay(forceOpenAfterMs, cts.Token).ConfigureAwait(false);
            failover.ForceOpen("runtime-stress");
            await Task.Delay(forceOpenHoldMs, cts.Token).ConfigureAwait(false);
            failover.ClearForcedOpen();
        }, cts.Token);

        var result = await RunMixedTrafficAsync(
            cache,
            distributed,
            "failover",
            workers,
            keyspace,
            TimeSpan.FromSeconds(durationSeconds),
            cts.Token).ConfigureAwait(false);

        await forceTask.ConfigureAwait(false);
        samplerCts.Cancel();
        await samplerTask.ConfigureAwait(false);

        var probeKey = CacheKey<string>.From($"vapecache:stress:probe:{Guid.NewGuid():N}");
        var (probe, _) = await WaitForRedisRecoveryAsync(cache, probeKey, backendState, cts.Token).ConfigureAwait(false);

        var cacheSnapshot = cacheStats.Snapshot;
        var originSnapshot = originStats.Snapshot;

        Assert.True(result.TotalOperations > 0, "Expected failover stress workload to execute operations.");
        Assert.True(
            result.TotalFailures < Math.Max(4, result.TotalOperations / 10),
            $"Forced failover degraded too far: ops={result.TotalOperations} fail={result.TotalFailures}");
        Assert.Contains(BackendType.InMemory, observedBackends);
        Assert.Contains(BackendType.Redis, observedBackends);
        Assert.True(cacheSnapshot.FallbackToMemory > 0, "Expected fallback traffic to be recorded while forced open.");
        Assert.True(cacheSnapshot.RedisBreakerOpened > 0, "Expected forced-open drill to increment breaker-open telemetry.");
        Assert.True(originSnapshot.NativeReads > 0, "Expected native reads to remain visible during failover stress.");
        Assert.True(originSnapshot.InteropReads > 0, "Expected bridge reads to remain visible during failover stress.");
        Assert.Equal("post-clear-probe", probe);
        Assert.False(failover.IsForcedOpen);
    }

    [SkippableFact]
    public async Task RepeatedFailoverCycles_DuringMixedTraffic_StayAvailable_AndKeepTelemetryHonest()
    {
        Skip.IfNot(IsStressEnabled(), "Set VAPECACHE_RUNTIME_STRESS_ENABLED=true to run runtime stress integration.");

        var redis = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(redis is null, skipReason);

        var workers = Math.Max(4, TryGetInt("VAPECACHE_RUNTIME_STRESS_WORKERS") ?? 24);
        var keyspace = Math.Max(16, TryGetInt("VAPECACHE_RUNTIME_STRESS_KEYSPACE") ?? 256);
        var durationSeconds = Math.Max(10, TryGetInt("VAPECACHE_RUNTIME_STRESS_DURATION_SECONDS") ?? 20);
        var forceOpenAfterMs = Math.Max(250, TryGetInt("VAPECACHE_RUNTIME_STRESS_FORCE_OPEN_AFTER_MS") ?? 1200);
        var forceOpenHoldMs = Math.Max(500, TryGetInt("VAPECACHE_RUNTIME_STRESS_FORCE_OPEN_HOLD_MS") ?? 1800);
        var cycles = Math.Max(2, TryGetInt("VAPECACHE_RUNTIME_STRESS_FORCE_OPEN_CYCLES") ?? 3);

        await using var provider = BuildRuntimeProvider(redis);
        var cache = provider.GetRequiredService<IVapeCache>();
        var distributed = provider.GetRequiredService<IDistributedCache>();
        var backendState = provider.GetRequiredService<ICacheBackendState>();
        var cacheStats = provider.GetRequiredService<ICacheStats>();
        var originStats = provider.GetRequiredService<ICacheOriginStats>();
        var failover = provider.GetRequiredService<IRedisFailoverController>();

        var observedBackends = new List<BackendType>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds + 45));
        using var samplerCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var samplerTask = Task.Run(async () =>
        {
            while (!samplerCts.IsCancellationRequested)
            {
                observedBackends.Add(backendState.EffectiveBackend);
                try
                {
                    await Task.Delay(100, samplerCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (samplerCts.IsCancellationRequested)
                {
                    break;
                }
            }
        }, samplerCts.Token);

        var cycleTask = Task.Run(async () =>
        {
            await Task.Delay(forceOpenAfterMs, cts.Token).ConfigureAwait(false);
            for (var cycle = 0; cycle < cycles; cycle++)
            {
                failover.ForceOpen($"runtime-soak-cycle-{cycle.ToString(CultureInfo.InvariantCulture)}");
                await Task.Delay(forceOpenHoldMs, cts.Token).ConfigureAwait(false);
                failover.ClearForcedOpen();

                if (cycle < cycles - 1)
                    await Task.Delay(forceOpenAfterMs, cts.Token).ConfigureAwait(false);
            }
        }, cts.Token);

        var result = await RunMixedTrafficAsync(
            cache,
            distributed,
            "soak",
            workers,
            keyspace,
            TimeSpan.FromSeconds(durationSeconds),
            cts.Token).ConfigureAwait(false);

        await cycleTask.ConfigureAwait(false);
        samplerCts.Cancel();
        await samplerTask.ConfigureAwait(false);

        var probeKey = CacheKey<string>.From($"vapecache:stress:soak-probe:{Guid.NewGuid():N}");
        var (probe, _) = await WaitForRedisRecoveryAsync(cache, probeKey, backendState, cts.Token).ConfigureAwait(false);
        var cacheSnapshot = cacheStats.Snapshot;
        var originSnapshot = originStats.Snapshot;

        Assert.True(result.TotalOperations > 0, "Expected soak workload to execute operations.");
        Assert.True(
            result.TotalFailures < Math.Max(8, result.TotalOperations / 8),
            $"Repeated failover cycles degraded too far: ops={result.TotalOperations} fail={result.TotalFailures}");
        Assert.Contains(BackendType.InMemory, observedBackends);
        Assert.Contains(BackendType.Redis, observedBackends);
        Assert.True(cacheSnapshot.FallbackToMemory >= cycles, "Expected fallback activity across repeated failover cycles.");
        Assert.True(cacheSnapshot.RedisBreakerOpened >= cycles, "Expected breaker-open telemetry across repeated forced-open cycles.");
        Assert.True(cacheSnapshot.SetCalls > 0, "Expected write traffic during soak run.");
        Assert.True(cacheSnapshot.GetCalls > 0, "Expected read traffic during soak run.");
        Assert.True(originSnapshot.NativeReads > 0, "Expected native reads during soak run.");
        Assert.True(originSnapshot.InteropReads > 0, "Expected distributed-cache reads during soak run.");
        Assert.True(originSnapshot.NativeWrites > 0, "Expected native writes during soak run.");
        Assert.True(originSnapshot.InteropWrites > 0, "Expected distributed-cache writes during soak run.");
        Assert.Equal("post-clear-probe", probe);
        Assert.False(failover.IsForcedOpen);
    }

    [SkippableFact]
    public async Task HotKeyContention_RecordsStampedeLockWaitTimeouts()
    {
        Skip.IfNot(IsStressEnabled(), "Set VAPECACHE_RUNTIME_STRESS_ENABLED=true to run runtime stress integration.");

        var redis = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(redis is null, skipReason);

        await using var provider = BuildRuntimeProvider(redis);
        var cache = provider.GetRequiredService<IVapeCache>();
        var cacheStats = provider.GetRequiredService<ICacheStats>();

        var key = CacheKey<string>.From($"vapecache:stress:stampede:{Guid.NewGuid():N}");
        var firstEnteredFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = cache.GetOrCreateAsync(
            key,
            async ct =>
            {
                firstEnteredFactory.TrySetResult();
                await releaseFirstFactory.Task.WaitAsync(ct).ConfigureAwait(false);
                await Task.Delay(1500, ct).ConfigureAwait(false);
                return "leader";
            },
            new CacheEntryOptions(TimeSpan.FromMinutes(1)),
            CancellationToken.None).AsTask();

        await firstEnteredFactory.Task.ConfigureAwait(false);

        var waiters = Enumerable.Range(0, 16)
            .Select(_ => cache.GetOrCreateAsync(
                key,
                _ => ValueTask.FromResult("waiter"),
                new CacheEntryOptions(TimeSpan.FromMinutes(1)),
                CancellationToken.None).AsTask())
            .ToArray();

        await Task.Delay(1000).ConfigureAwait(false);
        releaseFirstFactory.TrySetResult();

        var timeoutCount = 0;
        foreach (var waiter in waiters)
        {
            try
            {
                _ = await waiter.ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                timeoutCount++;
            }
        }

        var leaderValue = await first.ConfigureAwait(false);
        var stats = cacheStats.Snapshot;

        Assert.Equal("leader", leaderValue);
        Assert.True(timeoutCount > 0, "Expected at least one stampede waiter to time out.");
        Assert.True(stats.StampedeLockWaitTimeout > 0, "Expected stampede lock wait timeout telemetry to move.");
    }

    private static async Task<StressRunResult> RunMixedTrafficAsync(
        IVapeCache cache,
        IDistributedCache distributed,
        string scenario,
        int workers,
        int keyspace,
        TimeSpan duration,
        CancellationToken ct)
    {
        long totalOperations = 0;
        long totalFailures = 0;
        var keyPrefix = $"vapecache:stress:{scenario}:{Guid.NewGuid():N}:";
        var nativeKeys = new CacheKey<string>[keyspace];
        var interopKeys = new string[keyspace];
        for (var slot = 0; slot < keyspace; slot++)
        {
            var suffix = slot.ToString(CultureInfo.InvariantCulture);
            nativeKeys[slot] = CacheKey<string>.From($"{keyPrefix}native:{suffix}");
            interopKeys[slot] = $"{keyPrefix}interop:{suffix}";
        }

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        runCts.CancelAfter(duration);
        var runToken = runCts.Token;

        var tasks = Enumerable.Range(0, workers)
            .Select(workerId => Task.Run(async () =>
            {
                var random = new Random(unchecked((Environment.TickCount * 397) ^ workerId));
                var ttl = TimeSpan.FromSeconds(30);
                var nativePayload = $"native:{workerId.ToString(CultureInfo.InvariantCulture)}";
                var interopPayload = Encoding.UTF8.GetBytes($"interop:{workerId.ToString(CultureInfo.InvariantCulture)}");
                var distributedOptions = new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = ttl
                };

                while (!runToken.IsCancellationRequested)
                {
                    var slot = random.Next(0, keyspace);
                    try
                    {
                        if ((workerId & 1) == 0)
                        {
                            var key = nativeKeys[slot];
                            await cache.SetAsync(key, nativePayload, new CacheEntryOptions(ttl), runToken).ConfigureAwait(false);
                            var got = await cache.GetAsync(key, runToken).ConfigureAwait(false);
                            if (!string.Equals(nativePayload, got, StringComparison.Ordinal))
                                Interlocked.Increment(ref totalFailures);

                            if ((slot & 7) == 0)
                                _ = await cache.RemoveAsync(key, runToken).ConfigureAwait(false);
                        }
                        else
                        {
                            var key = interopKeys[slot];
                            await distributed.SetAsync(key, interopPayload, distributedOptions, runToken).ConfigureAwait(false);
                            var got = await distributed.GetAsync(key, runToken).ConfigureAwait(false);
                            if (got is null || !got.AsSpan().SequenceEqual(interopPayload))
                                Interlocked.Increment(ref totalFailures);

                            if ((slot & 3) == 0)
                                await distributed.RefreshAsync(key, runToken).ConfigureAwait(false);
                            if ((slot & 7) == 0)
                                await distributed.RemoveAsync(key, runToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (runToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch
                    {
                        Interlocked.Increment(ref totalFailures);
                    }
                    finally
                    {
                        Interlocked.Increment(ref totalOperations);
                    }
                }
            }, runToken))
            .ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested)
        {
        }

        return new StressRunResult(
            Interlocked.Read(ref totalOperations),
            Interlocked.Read(ref totalFailures));
    }

    private static async ValueTask<(string? Probe, bool RecoveredToRedis)> WaitForRedisRecoveryAsync(
        IVapeCache cache,
        CacheKey<string> probeKey,
        ICacheBackendState backendState,
        CancellationToken ct)
    {
        string? probe = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await cache.SetAsync(probeKey, "post-clear-probe", new CacheEntryOptions(TimeSpan.FromSeconds(30)), ct).ConfigureAwait(false);
            probe = await cache.GetAsync(probeKey, ct).ConfigureAwait(false);
            if (backendState.EffectiveBackend == BackendType.Redis)
                return (probe, true);

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return (probe, false);
    }

    private static ServiceProvider BuildRuntimeProvider(RedisConnectionOptions redis)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RedisConnection:Host"] = redis.Host,
                ["RedisConnection:Port"] = redis.Port.ToString(CultureInfo.InvariantCulture),
                ["RedisConnection:Username"] = redis.Username,
                ["RedisConnection:Password"] = redis.Password,
                ["RedisConnection:Database"] = redis.Database.ToString(CultureInfo.InvariantCulture),
                ["RedisConnection:ConnectionString"] = redis.ConnectionString,
                ["RedisConnection:UseTls"] = redis.UseTls.ToString(),
                ["RedisConnection:TlsHost"] = redis.TlsHost,
                ["RedisConnection:AllowInvalidCert"] = redis.AllowInvalidCert.ToString(),
                ["RedisConnection:AllowAuthFallbackToPasswordOnly"] = "true",
                ["RedisConnection:ConnectTimeout"] = "00:00:05",
                ["RedisConnection:AcquireTimeout"] = "00:00:05",
                ["RedisMultiplexer:Connections"] = (TryGetInt("VAPECACHE_RUNTIME_STRESS_CONNECTIONS") ?? 8).ToString(CultureInfo.InvariantCulture),
                ["RedisMultiplexer:MaxInFlightPerConnection"] = (TryGetInt("VAPECACHE_RUNTIME_STRESS_MAX_INFLIGHT") ?? 4096).ToString(CultureInfo.InvariantCulture),
                ["RedisMultiplexer:EnableCoalescedSocketWrites"] = "true",
                ["RedisCircuitBreaker:Enabled"] = "true",
                ["RedisCircuitBreaker:ConsecutiveFailuresToOpen"] = "2",
                ["RedisCircuitBreaker:BreakDuration"] = "00:00:02",
                ["RedisCircuitBreaker:HalfOpenProbeTimeout"] = "00:00:01",
                ["HybridFailover:WarmFallbackOnRedisReadHit"] = "true",
                ["HybridFailover:MirrorWritesToFallbackWhenRedisHealthy"] = "true",
                ["HybridFailover:FallbackWarmReadTtl"] = "00:00:20",
                ["HybridFailover:FallbackMirrorWriteTtlWhenMissing"] = "00:00:20"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddVapeCache(configuration);
        services.AddVapeCacheDistributedCache();
        return services.BuildServiceProvider();
    }

    private static bool IsStressEnabled()
        => TryGetBool("VAPECACHE_RUNTIME_STRESS_ENABLED") ?? false;

    private static int? TryGetInt(string key)
        => int.TryParse(Environment.GetEnvironmentVariable(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static bool? TryGetBool(string key)
        => bool.TryParse(Environment.GetEnvironmentVariable(key), out var value)
            ? value
            : null;

    private readonly record struct StressRunResult(long TotalOperations, long TotalFailures);
}
