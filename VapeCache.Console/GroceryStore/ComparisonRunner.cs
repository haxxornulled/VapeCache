using Autofac;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Globalization;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Features.Invalidation;
using VapeCache.Features.Search;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;
using StackExchange.Redis;

namespace VapeCache.Console.GroceryStore;

/// <summary>
/// Runs head-to-head comparison between the shared grocery store and two providers:
/// VapeCache Native and SER.
/// </summary>
public static class ComparisonRunner
{
    private enum GroceryComparisonTrack
    {
        ApplesToApples,
        OptimizedProductPath,
        Both
    }

    private enum ComparisonProvider
    {
        VapeCacheNative,
        Ser
    }

    private enum VapeExecutorMode
    {
        Raw,
        HybridFailover
    }

    private const string VapeCacheNativeProviderName = "VapeCache Native";
    private const string SerProviderName = "SER";

    private readonly record struct HarnessSettings
    {
        public HarnessSettings(
            int Runs,
            int WarmupRuns,
            bool AlternateOrder,
            int DeterministicSeed,
            bool CleanupRunKeys,
            TimeSpan ProviderTimeout,
            LogLevel BenchmarkLogLevel,
            int? MaxDegreeOfParallelism,
            bool LiveProgressEnabled,
            TimeSpan LiveProgressInterval)
        {
            this.Runs = Runs;
            this.WarmupRuns = WarmupRuns;
            this.AlternateOrder = AlternateOrder;
            this.DeterministicSeed = DeterministicSeed;
            this.CleanupRunKeys = CleanupRunKeys;
            this.ProviderTimeout = ProviderTimeout;
            this.BenchmarkLogLevel = BenchmarkLogLevel;
            this.MaxDegreeOfParallelism = MaxDegreeOfParallelism;
            this.LiveProgressEnabled = LiveProgressEnabled;
            this.LiveProgressInterval = LiveProgressInterval;
        }

        public int Runs { get; init; }
        public int WarmupRuns { get; init; }
        public bool AlternateOrder { get; init; }
        public int DeterministicSeed { get; init; }
        public bool CleanupRunKeys { get; init; }
        public TimeSpan ProviderTimeout { get; init; }
        public LogLevel BenchmarkLogLevel { get; init; }
        public int? MaxDegreeOfParallelism { get; init; }
        public bool LiveProgressEnabled { get; init; }
        public TimeSpan LiveProgressInterval { get; init; }
    }

    /// <summary>
    /// Runs value.
    /// </summary>
    public static async Task RunComparisonAsync(
        IConfiguration configuration,
        string redisHost,
        int redisPort,
        string? redisUsername,
        string redisPassword,
        int shopperCount = 10_000,
        int minCartSize = 30,
        int maxCartSize = 50)
    {
        System.Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        System.Console.WriteLine("║           VapeCache Native vs SER Showdown                 ║");
        System.Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        System.Console.WriteLine();

        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        System.Console.WriteLine($"[RunId] {runId}");
        System.Console.WriteLine();

        var benchTrack = GetTrackFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_TRACK"),
            configuration["GroceryStoreComparison:BenchTrack"],
            GroceryComparisonTrack.ApplesToApples);
        var cleanupBenchKeys = GetBoolFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_CLEANUP"),
            configuration["GroceryStoreComparison:CleanupBenchmarkKeys"],
            false);
        var harness = new HarnessSettings(
            Runs: GetIntFromSources(
                Environment.GetEnvironmentVariable("VAPECACHE_BENCH_RUNS"),
                configuration["GroceryStoreComparison:Runs"],
                3),
            WarmupRuns: GetIntFromSources(
                Environment.GetEnvironmentVariable("VAPECACHE_BENCH_WARMUPS"),
                configuration["GroceryStoreComparison:WarmupRuns"],
                1,
                allowZero: true),
            AlternateOrder: GetBoolFromSources(
                Environment.GetEnvironmentVariable("VAPECACHE_BENCH_ALTERNATE_ORDER"),
                configuration["GroceryStoreComparison:AlternateOrder"],
                true),
            DeterministicSeed: GetIntFromSources(
                Environment.GetEnvironmentVariable("VAPECACHE_BENCH_DETERMINISTIC_SEED"),
                configuration["GroceryStoreComparison:DeterministicSeed"],
                1337,
                allowZero: true),
            CleanupRunKeys: GetBoolFromSources(
                Environment.GetEnvironmentVariable("VAPECACHE_BENCH_CLEANUP_RUN_KEYS"),
                configuration["GroceryStoreComparison:CleanupRunKeys"],
                true),
            ProviderTimeout: TimeSpan.FromSeconds(GetIntFromSources(
                Environment.GetEnvironmentVariable("VAPECACHE_BENCH_PROVIDER_TIMEOUT_SECONDS"),
                configuration["GroceryStoreComparison:ProviderTimeoutSeconds"],
                0,
                allowZero: true)),
            BenchmarkLogLevel: GetLogLevelFromSources(
                Environment.GetEnvironmentVariable("VAPECACHE_BENCH_LOG_LEVEL"),
                configuration["GroceryStoreComparison:LogLevel"],
                LogLevel.Warning),
            MaxDegreeOfParallelism: GetNullableIntFromSources(
                Environment.GetEnvironmentVariable("VAPECACHE_BENCH_MAX_DEGREE"),
                configuration["GroceryStoreComparison:MaxDegreeOfParallelism"]),
            LiveProgressEnabled: GetBoolFromSources(
                Environment.GetEnvironmentVariable("VAPECACHE_COMPARE_LIVE_PROGRESS"),
                configuration["GroceryStoreComparison:LiveProgressEnabled"],
                false),
            LiveProgressInterval: GetTimeSpanFromSources(
                Environment.GetEnvironmentVariable("VAPECACHE_COMPARE_LIVE_INTERVAL_SECONDS"),
                configuration["GroceryStoreComparison:LiveProgressIntervalSeconds"],
                TimeSpan.FromSeconds(15)));

        if (cleanupBenchKeys)
        {
            var deleted = await CleanupBenchmarkKeysAsync(redisHost, redisPort, redisUsername, redisPassword, "cmp:*").ConfigureAwait(false);
            System.Console.WriteLine($"[BenchCleanup] Deleted {deleted:N0} stale comparison keys (pattern: cmp:*).");
            System.Console.WriteLine();
        }

        System.Console.WriteLine($"[BenchTrack] {benchTrack}");
        System.Console.WriteLine("Scenario Definitions:");
        System.Console.WriteLine("  ApplesToApples: shared SuperCenter store code with provider swap only.");
        System.Console.WriteLine("  OptimizedProductPath: same SuperCenter store code, with VapeCache native runtime knobs tuned.");
        PrintBenchmarkHeader(redisHost, redisPort, shopperCount, minCartSize, maxCartSize, benchTrack, harness);
        System.Console.WriteLine();

        if (benchTrack == GroceryComparisonTrack.Both)
        {
            System.Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            System.Console.WriteLine("║                    COMPARISON RESULTS                        ║");
            System.Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            System.Console.WriteLine($"Aggregation: median-of-{harness.Runs} measured runs after {harness.WarmupRuns} warmup run(s).");
            System.Console.WriteLine();

            System.Console.WriteLine("Track: ApplesToApples");
            var parity = await RunTrackComparisonAsync(
                configuration,
                redisHost,
                redisPort,
                redisUsername,
                redisPassword,
                shopperCount,
                minCartSize,
                maxCartSize,
                GroceryComparisonTrack.ApplesToApples,
                runId,
                harness).ConfigureAwait(false);
            PrintComparison(parity.VapeCache, parity.StackExchange);
            System.Console.WriteLine();
            System.Console.WriteLine("Track: OptimizedProductPath");
            var optimized = await RunTrackComparisonAsync(
                configuration,
                redisHost,
                redisPort,
                redisUsername,
                redisPassword,
                shopperCount,
                minCartSize,
                maxCartSize,
                GroceryComparisonTrack.OptimizedProductPath,
                runId,
                harness).ConfigureAwait(false);
            PrintComparison(optimized.VapeCache, optimized.StackExchange);
            return;
        }

        var result = await RunTrackComparisonAsync(
            configuration,
            redisHost,
            redisPort,
            redisUsername,
            redisPassword,
            shopperCount,
            minCartSize,
            maxCartSize,
            benchTrack,
            runId,
            harness).ConfigureAwait(false);
        System.Console.WriteLine();
        System.Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        System.Console.WriteLine("║                    COMPARISON RESULTS                        ║");
        System.Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        System.Console.WriteLine($"Aggregation: median-of-{harness.Runs} measured runs after {harness.WarmupRuns} warmup run(s).");
        System.Console.WriteLine();
        PrintComparison(result.VapeCache, result.StackExchange);
    }

    private static async Task<(StressTestResult VapeCache, StressTestResult StackExchange)> RunTrackComparisonAsync(
        IConfiguration configuration,
        string redisHost,
        int redisPort,
        string? redisUsername,
        string redisPassword,
        int shopperCount,
        int minCartSize,
        int maxCartSize,
        GroceryComparisonTrack track,
        string runId,
        HarnessSettings harness)
    {
        var measuredVape = new List<StressTestResult>(harness.Runs);
        var measuredSer = new List<StressTestResult>(harness.Runs);
        var totalIterations = harness.WarmupRuns + harness.Runs;

        for (var iteration = 0; iteration < totalIterations; iteration++)
        {
            var warmup = iteration < harness.WarmupRuns;
            var phase = warmup ? "warmup" : "measured";
            var sequence = GetExecutionOrder(harness.AlternateOrder, iteration);
            var deterministicSeed = unchecked(harness.DeterministicSeed + (iteration * 7919) + ((int)track * 257));
            var iterationPrefixes = new List<string>(2);

            System.Console.WriteLine($"[Harness] Iteration {iteration + 1}/{totalIterations} ({phase}), Seed={deterministicSeed}, Order={sequence[0]} -> {sequence[1]}");

            try
            {
                foreach (var provider in sequence)
                {
                    if (provider == ComparisonProvider.VapeCacheNative)
                    {
                        var prefix = $"cmp:{runId}:{track}:vape:i{iteration:D2}:";
                        iterationPrefixes.Add(prefix);
                        iterationPrefixes.Add(string.Concat(
                            SuperCenterReceiptSearch.ComparisonDocumentKeyPrefix("vape"),
                            prefix));
                        var result = await RunVapeCacheTestAsync(
                            configuration,
                            redisHost,
                            redisPort,
                            redisUsername,
                            redisPassword,
                            shopperCount,
                            minCartSize,
                            maxCartSize,
                            track,
                            prefix,
                            deterministicSeed,
                            harness).ConfigureAwait(false);
                        PrintMachineResult(result, track);
                        if (!warmup)
                            measuredVape.Add(result);
                    }
                    else
                    {
                        var prefix = $"cmp:{runId}:{track}:ser:i{iteration:D2}:";
                        iterationPrefixes.Add(prefix);
                        iterationPrefixes.Add(string.Concat(
                            SuperCenterReceiptSearch.ComparisonDocumentKeyPrefix("ser"),
                            prefix));
                        var result = await RunStackExchangeRedisTestAsync(
                            configuration,
                            redisHost,
                            redisPort,
                            redisUsername,
                            redisPassword,
                            shopperCount,
                            minCartSize,
                            maxCartSize,
                            track,
                            prefix,
                            deterministicSeed,
                            harness).ConfigureAwait(false);
                        PrintMachineResult(result, track);
                        if (!warmup)
                            measuredSer.Add(result);
                    }
                }
            }
            finally
            {
                if (harness.CleanupRunKeys)
                {
                    foreach (var prefix in iterationPrefixes)
                    {
                        await CleanupRunKeysSafelyAsync(redisHost, redisPort, redisUsername, redisPassword, prefix).ConfigureAwait(false);
                    }
                }
            }

            System.Console.WriteLine();
            if (warmup)
                System.Console.WriteLine("════════════════════════════ End Warmup ═══════════════════════");
            else
                System.Console.WriteLine("══════════════════════════ End Measured Run ═══════════════════");
            System.Console.WriteLine();
        }

        var vapeAggregated = AggregateMedianResult(VapeCacheNativeProviderName, shopperCount, measuredVape);
        var serAggregated = AggregateMedianResult(SerProviderName, shopperCount, measuredSer);
        return (vapeAggregated, serAggregated);
    }

    private static ComparisonProvider[] GetExecutionOrder(bool alternateOrder, int iteration)
    {
        if (!alternateOrder || (iteration % 2) == 0)
            return [ComparisonProvider.VapeCacheNative, ComparisonProvider.Ser];

        return [ComparisonProvider.Ser, ComparisonProvider.VapeCacheNative];
    }

    private static StressTestResult AggregateMedianResult(
        string providerName,
        int shopperCount,
        IReadOnlyList<StressTestResult> results)
    {
        if (results.Count == 0)
            throw new InvalidOperationException("No measured benchmark runs were captured.");

        return new StressTestResult(
            ProviderName: providerName,
            ShopperCount: shopperCount,
            SuccessCount: Median(results.Select(static r => r.SuccessCount).ToArray()),
            ErrorCount: Median(results.Select(static r => r.ErrorCount).ToArray()),
            TotalDuration: TimeSpan.FromTicks(Median(results.Select(static r => r.TotalDuration.Ticks).ToArray())),
            ShopperDuration: TimeSpan.FromTicks(Median(results.Select(static r => r.ShopperDuration.Ticks).ToArray())),
            PreCacheDuration: TimeSpan.FromTicks(Median(results.Select(static r => r.PreCacheDuration.Ticks).ToArray())),
            AverageCartSize: Median(results.Select(static r => r.AverageCartSize).ToArray()),
            AverageLatencyMs: Median(results.Select(static r => r.AverageLatencyMs).ToArray()),
            P50LatencyMs: Median(results.Select(static r => r.P50LatencyMs).ToArray()),
            P95LatencyMs: Median(results.Select(static r => r.P95LatencyMs).ToArray()),
            P99LatencyMs: Median(results.Select(static r => r.P99LatencyMs).ToArray()),
            P999LatencyMs: Median(results.Select(static r => r.P999LatencyMs).ToArray()),
            ThroughputShoppersPerSec: Median(results.Select(static r => r.ThroughputShoppersPerSec).ToArray()),
            AllocatedBytes: Median(results.Select(static r => r.AllocatedBytes).ToArray()),
            Gen0Collections: Median(results.Select(static r => r.Gen0Collections).ToArray()),
            Gen1Collections: Median(results.Select(static r => r.Gen1Collections).ToArray()),
            Gen2Collections: Median(results.Select(static r => r.Gen2Collections).ToArray()),
            ServiceReadOps: Median(results.Select(static r => r.ServiceReadOps).ToArray()),
            ServiceWriteOps: Median(results.Select(static r => r.ServiceWriteOps).ToArray()),
            ServiceTotalOps: Median(results.Select(static r => r.ServiceTotalOps).ToArray()),
            ServiceCartItemWriteOps: Median(results.Select(static r => r.ServiceCartItemWriteOps).ToArray()),
            ServiceAdminOps: Median(results.Select(static r => r.ServiceAdminOps).ToArray()),
            ServiceOptionalSkips: Median(results.Select(static r => r.ServiceOptionalSkips).ToArray()));
    }

    private static async Task<StressTestResult> RunVapeCacheTestAsync(
        IConfiguration configuration,
        string redisHost,
        int redisPort,
        string? redisUsername,
        string redisPassword,
        int shopperCount,
        int minCartSize,
        int maxCartSize,
        GroceryComparisonTrack track,
        string keyPrefix,
        int deterministicSeed,
        HarnessSettings harness)
    {
        var services = new ServiceCollection();
        var muxConnections = GetIntFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_MUX_CONNECTIONS"),
            configuration["GroceryStoreComparison:MuxConnections"],
            12);
        var muxInFlight = GetIntFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_MUX_INFLIGHT"),
            configuration["GroceryStoreComparison:MuxInFlight"],
            4096);
        var muxResponseTimeoutMs = GetIntFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_MUX_RESPONSE_TIMEOUT_MS"),
            configuration["GroceryStoreComparison:MuxResponseTimeoutMs"],
            0,
            allowZero: true);
        var muxCoalesce = GetBoolFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_MUX_COALESCE"),
            configuration["GroceryStoreComparison:MuxCoalesce"],
            true);
        var muxAdaptiveCoalescing = GetBoolFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_MUX_ADAPTIVE_COALESCING"),
            configuration["GroceryStoreComparison:MuxAdaptiveCoalescing"],
            true);
        var muxSocketRespReader = GetBoolFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_SOCKET_RESP_READER"),
            configuration["GroceryStoreComparison:MuxSocketRespReader"],
            true);
        var muxDedicatedLaneWorkers = GetBoolFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_DEDICATED_LANE_WORKERS"),
            configuration["GroceryStoreComparison:MuxDedicatedLaneWorkers"],
            true);
        var muxEnableSpillPressureSignals = GetBoolFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_ENABLE_SPILL_PRESSURE_SIGNALS"),
            configuration["GroceryStoreComparison:MuxEnableSpillPressureSignals"],
            true);
        var muxSpillPressureTotalFilesThreshold = GetIntFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_SPILL_FILES_THRESHOLD"),
            configuration["GroceryStoreComparison:MuxSpillPressureTotalFilesThreshold"],
            4000);
        var muxSpillPressureActiveShardsThreshold = GetIntFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_SPILL_ACTIVE_SHARDS_THRESHOLD"),
            configuration["GroceryStoreComparison:MuxSpillPressureActiveShardsThreshold"],
            48);
        var muxSpillPressureImbalanceRatioThreshold = GetDoubleFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_SPILL_IMBALANCE_RATIO_THRESHOLD"),
            configuration["GroceryStoreComparison:MuxSpillPressureImbalanceRatioThreshold"],
            1.75d);
        var muxSpillPressureSustainedWindow = GetTimeSpanFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_SPILL_SUSTAINED_WINDOW_SECONDS"),
            configuration["GroceryStoreComparison:MuxSpillPressureSustainedWindowSeconds"],
            TimeSpan.FromSeconds(20));
        var muxProfile = GetTransportProfileFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_MUX_PROFILE"),
            configuration["GroceryStoreComparison:MuxProfile"],
            RedisTransportProfile.FullTilt);
        var muxBulkLaneConnections = muxConnections <= 1
            ? 0
            : Math.Max(1, Math.Min(muxConnections - 1, muxConnections / 4));
        var executorMode = GetVapeExecutorModeFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_VAPE_EXECUTOR_MODE"),
            configuration["GroceryStoreComparison:VapeExecutorMode"],
            VapeExecutorMode.HybridFailover);
        var enableDiskSpill = GetBoolFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_ENABLE_DISK_SPILL"),
            configuration["GroceryStoreComparison:EnableDiskSpill"],
            false);
        var spillThresholdBytes = GetIntFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_SPILL_THRESHOLD_BYTES"),
            configuration["GroceryStoreComparison:SpillThresholdBytes"],
            256 * 1024);
        var spillPrimeRecords = GetIntFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_SPILL_PRIME_RECORDS"),
            configuration["GroceryStoreComparison:SpillPrimeRecords"],
            0,
            allowZero: true);
        var spillPrimePayloadBytes = GetIntFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_SPILL_PRIME_PAYLOAD_BYTES"),
            configuration["GroceryStoreComparison:SpillPrimePayloadBytes"],
            64 * 1024);
        var spillDirectory = Environment.GetEnvironmentVariable("VAPECACHE_BENCH_SPILL_DIRECTORY");
        if (string.IsNullOrWhiteSpace(spillDirectory))
            spillDirectory = configuration["GroceryStoreComparison:SpillDirectory"];
        var hybridFastPath = GetBoolFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_HYBRID_FAST_PATH"),
            configuration["GroceryStoreComparison:HybridFastPath"],
            true);
        var hybridAdmissionGate = GetBoolFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_HYBRID_ADMISSION_GATE"),
            configuration["GroceryStoreComparison:HybridAdmissionGate"],
            false);
        var hybridAdmissionLimit = GetIntFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_HYBRID_ADMISSION_LIMIT"),
            configuration["GroceryStoreComparison:HybridAdmissionLimit"],
            0,
            allowZero: true);
        var hybridAdmissionWaitMs = GetIntFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_HYBRID_ADMISSION_WAIT_MS"),
            configuration["GroceryStoreComparison:HybridAdmissionWaitMs"],
            2,
            allowZero: true);
        var hybridMirrorWrites = GetBoolFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_HYBRID_MIRROR_WRITES"),
            configuration["GroceryStoreComparison:HybridMirrorWrites"],
            false);
        var hybridWarmReadFallback = GetBoolFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_HYBRID_WARM_READ_FALLBACK"),
            configuration["GroceryStoreComparison:HybridWarmReadFallback"],
            false);
        var hybridRemoveStaleFallbackOnMiss = GetBoolFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_HYBRID_REMOVE_STALE_FALLBACK_ON_MISS"),
            configuration["GroceryStoreComparison:HybridRemoveStaleFallbackOnMiss"],
            false);
        var checkoutLaneCount = GetIntFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_CHECKOUT_LANES"),
            configuration["GroceryStoreComparison:CheckoutLanes"],
            128);

        Environment.SetEnvironmentVariable("VAPECACHE_HYBRID_FAST_HEALTHY_PATH", hybridFastPath ? "1" : "0");
        Environment.SetEnvironmentVariable("VAPECACHE_HYBRID_ADMISSION_GATE", hybridAdmissionGate ? "1" : "0");
        Environment.SetEnvironmentVariable("VAPECACHE_HYBRID_PRIMARY_ADMISSION_LIMIT", hybridAdmissionLimit.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("VAPECACHE_HYBRID_PRIMARY_ADMISSION_WAIT_MS", hybridAdmissionWaitMs.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("HybridFailover__MirrorWritesToFallbackWhenRedisHealthy", hybridMirrorWrites ? "true" : "false");
        Environment.SetEnvironmentVariable("HybridFailover__WarmFallbackOnRedisReadHit", hybridWarmReadFallback ? "true" : "false");
        Environment.SetEnvironmentVariable("HybridFailover__RemoveStaleFallbackOnRedisMiss", hybridRemoveStaleFallbackOnMiss ? "true" : "false");

        System.Console.WriteLine(
            $"[VapeConfig] ExecutorMode={executorMode}, Mux.Profile={muxProfile}, Mux.Connections={muxConnections}, Mux.BulkLanes={muxBulkLaneConnections}, Mux.MaxInFlight={muxInFlight}, Mux.Coalesce={muxCoalesce}, Mux.AdaptiveCoalesce={muxAdaptiveCoalescing}, Mux.SocketReader={muxSocketRespReader}, Mux.DedicatedWorkers={muxDedicatedLaneWorkers}, Mux.ResponseTimeoutMs={muxResponseTimeoutMs}, SpillSignals={muxEnableSpillPressureSignals}, SpillFilesThreshold={muxSpillPressureTotalFilesThreshold}, SpillActiveShardsThreshold={muxSpillPressureActiveShardsThreshold}, SpillImbalanceThreshold={muxSpillPressureImbalanceRatioThreshold:F2}, SpillSustainedWindow={muxSpillPressureSustainedWindow.TotalSeconds:N0}s");
        System.Console.WriteLine(
            $"[HybridConfig] FastPath={hybridFastPath}, AdmissionGate={hybridAdmissionGate}, AdmissionLimit={hybridAdmissionLimit}, AdmissionWaitMs={hybridAdmissionWaitMs}, MirrorWrites={hybridMirrorWrites}, WarmReadFallback={hybridWarmReadFallback}, RemoveStaleFallbackOnMiss={hybridRemoveStaleFallbackOnMiss}");
        System.Console.WriteLine(
            $"[SpillConfig] DiskSpill={enableDiskSpill}, ThresholdBytes={spillThresholdBytes}, SpillDir={spillDirectory ?? "<default>"}, PrimeRecords={spillPrimeRecords}, PrimePayloadBytes={spillPrimePayloadBytes}");
        System.Console.WriteLine($"[VapeConfig] KeyPrefix={keyPrefix}");
        System.Console.WriteLine($"[WorkloadConfig] CheckoutLanes={checkoutLaneCount}");

        // Logging
        services.AddLogging(builder => ConfigureBenchmarkLogging(builder, harness.BenchmarkLogLevel));

        // VapeCache setup
        services.AddOptions<VapeCache.Abstractions.Connections.RedisConnectionOptions>()
            .Configure(options =>
            {
                typeof(VapeCache.Abstractions.Connections.RedisConnectionOptions)
                    .GetProperty(nameof(VapeCache.Abstractions.Connections.RedisConnectionOptions.Host))!
                    .SetValue(options, redisHost);
                typeof(VapeCache.Abstractions.Connections.RedisConnectionOptions)
                    .GetProperty(nameof(VapeCache.Abstractions.Connections.RedisConnectionOptions.Port))!
                    .SetValue(options, redisPort);
                var useAuth = !string.IsNullOrWhiteSpace(redisPassword);
                typeof(VapeCache.Abstractions.Connections.RedisConnectionOptions)
                    .GetProperty(nameof(VapeCache.Abstractions.Connections.RedisConnectionOptions.Username))!
                    .SetValue(options, useAuth ? redisUsername : null);
                typeof(VapeCache.Abstractions.Connections.RedisConnectionOptions)
                    .GetProperty(nameof(VapeCache.Abstractions.Connections.RedisConnectionOptions.Password))!
                    .SetValue(options, useAuth ? redisPassword : null);
                typeof(VapeCache.Abstractions.Connections.RedisConnectionOptions)
                    .GetProperty(nameof(VapeCache.Abstractions.Connections.RedisConnectionOptions.AllowAuthFallbackToPasswordOnly))!
                    .SetValue(options, false);
                typeof(VapeCache.Abstractions.Connections.RedisConnectionOptions)
                    .GetProperty(nameof(VapeCache.Abstractions.Connections.RedisConnectionOptions.LogWhoAmIOnConnect))!
                    .SetValue(options, false);
                typeof(VapeCache.Abstractions.Connections.RedisConnectionOptions)
                    .GetProperty(nameof(VapeCache.Abstractions.Connections.RedisConnectionOptions.MaxConnections))!
                    .SetValue(options, 128);
                typeof(VapeCache.Abstractions.Connections.RedisConnectionOptions)
                    .GetProperty(nameof(VapeCache.Abstractions.Connections.RedisConnectionOptions.MaxIdle))!
                    .SetValue(options, 128);
                typeof(VapeCache.Abstractions.Connections.RedisConnectionOptions)
                    .GetProperty(nameof(VapeCache.Abstractions.Connections.RedisConnectionOptions.Warm))!
                    .SetValue(options, 32);
            });
        services.AddOptions<RedisMultiplexerOptions>().Configure(options =>
        {
            typeof(RedisMultiplexerOptions)
                .GetProperty(nameof(RedisMultiplexerOptions.EnableCommandInstrumentation))!
                .SetValue(options, false);
            typeof(RedisMultiplexerOptions)
                .GetProperty(nameof(RedisMultiplexerOptions.TransportProfile))!
                .SetValue(options, muxProfile);
            typeof(RedisMultiplexerOptions)
                .GetProperty(nameof(RedisMultiplexerOptions.Connections))!
                .SetValue(options, muxConnections);
            typeof(RedisMultiplexerOptions)
                .GetProperty(nameof(RedisMultiplexerOptions.BulkLaneConnections))!
                .SetValue(options, muxBulkLaneConnections);
            typeof(RedisMultiplexerOptions)
                .GetProperty(nameof(RedisMultiplexerOptions.AutoAdjustBulkLanes))!
                .SetValue(options, false);
            typeof(RedisMultiplexerOptions)
                .GetProperty(nameof(RedisMultiplexerOptions.MaxInFlightPerConnection))!
                .SetValue(options, muxInFlight);
            typeof(RedisMultiplexerOptions)
                .GetProperty(nameof(RedisMultiplexerOptions.EnableCoalescedSocketWrites))!
                .SetValue(options, muxCoalesce);
            typeof(RedisMultiplexerOptions)
                .GetProperty(nameof(RedisMultiplexerOptions.EnableAdaptiveCoalescing))!
                .SetValue(options, muxAdaptiveCoalescing);
            typeof(RedisMultiplexerOptions)
                .GetProperty(nameof(RedisMultiplexerOptions.EnableSocketRespReader))!
                .SetValue(options, muxSocketRespReader);
            typeof(RedisMultiplexerOptions)
                .GetProperty(nameof(RedisMultiplexerOptions.UseDedicatedLaneWorkers))!
                .SetValue(options, muxDedicatedLaneWorkers);
            typeof(RedisMultiplexerOptions)
                .GetProperty(nameof(RedisMultiplexerOptions.ResponseTimeout))!
                .SetValue(options, muxResponseTimeoutMs <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(muxResponseTimeoutMs));
            typeof(RedisMultiplexerOptions)
                .GetProperty(nameof(RedisMultiplexerOptions.BulkLaneResponseTimeout))!
                .SetValue(options, muxResponseTimeoutMs <= 0 ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(muxResponseTimeoutMs));
            typeof(RedisMultiplexerOptions)
                .GetProperty(nameof(RedisMultiplexerOptions.EnableSpillPressureSignals))!
                .SetValue(options, muxEnableSpillPressureSignals);
            typeof(RedisMultiplexerOptions)
                .GetProperty(nameof(RedisMultiplexerOptions.SpillPressureTotalFilesThreshold))!
                .SetValue(options, muxSpillPressureTotalFilesThreshold);
            typeof(RedisMultiplexerOptions)
                .GetProperty(nameof(RedisMultiplexerOptions.SpillPressureActiveShardsThreshold))!
                .SetValue(options, muxSpillPressureActiveShardsThreshold);
            typeof(RedisMultiplexerOptions)
                .GetProperty(nameof(RedisMultiplexerOptions.SpillPressureImbalanceRatioThreshold))!
                .SetValue(options, muxSpillPressureImbalanceRatioThreshold);
            typeof(RedisMultiplexerOptions)
                .GetProperty(nameof(RedisMultiplexerOptions.SpillPressureSustainedWindow))!
                .SetValue(options, muxSpillPressureSustainedWindow);
        });
        services.AddOptions<CacheStampedeOptions>().Configure(options =>
        {
            typeof(CacheStampedeOptions)
                .GetProperty(nameof(CacheStampedeOptions.Enabled))!
                .SetValue(options, false);
        });

        services.AddVapecacheRedisConnections();
        services.AddVapecacheCaching();
        var receiptSearchRuntime = ReceiptSearchRuntimeDescriptor.ForComparison("vape");
        services.AddVapeCacheSearch(configure: options =>
        {
            options.Enabled = true;
            options.RequireModuleAvailability = true;
            options.DefaultResultCount = 3;
        });
        services.AddSingleton<IRedisHashSearchDocumentMapper<ReceiptSearchDocument>>(
            _ => new ReceiptSearchDocumentMapper(receiptSearchRuntime, keyPrefix));
        if (enableDiskSpill)
            services.AddVapeCachePersistence();

        services.AddOptions<InMemorySpillOptions>().Configure(options =>
        {
            options.EnableSpillToDisk = enableDiskSpill;
            options.SpillThresholdBytes = Math.Max(1, spillThresholdBytes);
            if (!string.IsNullOrWhiteSpace(spillDirectory))
                options.SpillDirectory = spillDirectory;
        });

        if (executorMode == VapeExecutorMode.Raw)
        {
            services.AddOptions<RedisCircuitBreakerOptions>().Configure(options =>
            {
                typeof(RedisCircuitBreakerOptions)
                    .GetProperty(nameof(RedisCircuitBreakerOptions.Enabled))!
                    .SetValue(options, false);
            });

            // Keep raw mode available for transport-only drills while retaining HybridCache APIs.
            services.AddSingleton<IRedisCommandExecutor>(sp =>
            {
                var executorType = typeof(RedisConnectionRegistration).Assembly.GetType(
                    "VapeCache.Infrastructure.Connections.RedisCommandExecutor",
                    throwOnError: true)!;
                return (IRedisCommandExecutor)sp.GetRequiredService(executorType);
            });
        }

        AddSharedComparisonStore(
            services,
            sp => new VapeCacheSuperCenterProvider(
                sp.GetRequiredService<IRedisCommandExecutor>(),
                keyPrefix,
                sp.GetRequiredService<IRedisHashSearchDocumentStore<ReceiptSearchDocument>>(),
                keyPrefix),
            receiptSearchRuntime);

        using var provider = BuildAutofacServiceProvider(services);

        if (executorMode == VapeExecutorMode.HybridFailover &&
            enableDiskSpill &&
            spillPrimeRecords > 0 &&
            spillPrimePayloadBytes > 0)
        {
            await PrimeSpillStoreAsync(provider, spillPrimeRecords, spillPrimePayloadBytes, CancellationToken.None).ConfigureAwait(false);
        }

        var service = provider.GetRequiredService<IGroceryStoreService>();
        var logger = provider.GetRequiredService<ILogger<GroceryStoreComparisonStressTest>>();

        var providerName = VapeCacheNativeProviderName;
        var test = new GroceryStoreComparisonStressTest(
            service,
            logger,
            providerName,
            deterministicSeed,
            harness.MaxDegreeOfParallelism,
            checkoutLaneCount,
            harness.LiveProgressEnabled ? harness.LiveProgressInterval : null,
            CreateLiveProgressSink(track, providerName, harness));
        return await RunWithOptionalTimeoutAsync(
                ct => test.RunStressTestAsync(shopperCount, minCartSize, maxCartSize, ct),
                harness.ProviderTimeout,
                providerName)
            .ConfigureAwait(false);
    }

    private static async Task PrimeSpillStoreAsync(
        IServiceProvider provider,
        int records,
        int payloadBytes,
        CancellationToken ct)
    {
        var spillStore = provider.GetService<IInMemorySpillStore>();
        if (spillStore is null)
            return;

        var count = Math.Max(0, records);
        var size = Math.Max(1, payloadBytes);
        var payload = GC.AllocateUninitializedArray<byte>(size);
        for (var i = 0; i < payload.Length; i++)
            payload[i] = unchecked((byte)(i * 31 + 17));

        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            await spillStore.WriteAsync(Guid.NewGuid(), payload, ct).ConfigureAwait(false);
        }

        System.Console.WriteLine($"[SpillPrime] Wrote {count:N0} records of {size:N0} bytes to spill store.");
    }

    private static void PrintBenchmarkHeader(
        string redisHost,
        int redisPort,
        int shopperCount,
        int minCartSize,
        int maxCartSize,
        GroceryComparisonTrack track,
        HarnessSettings harness)
    {
        var framework = RuntimeInformation.FrameworkDescription;
        var os = RuntimeInformation.OSDescription.Replace('|', '/');
        var arch = RuntimeInformation.ProcessArchitecture.ToString();
        var cpuLogicalCores = Environment.ProcessorCount;
        var serverGc = GCSettings.IsServerGC;
        var totalMemoryMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0);

        System.Console.WriteLine("[BenchmarkHeader]");
        System.Console.WriteLine($"Framework: {framework}");
        System.Console.WriteLine($"OS: {os}");
        System.Console.WriteLine($"Arch: {arch}");
        System.Console.WriteLine($"CPU Logical Cores: {cpuLogicalCores}");
        System.Console.WriteLine($"GC Mode: {(serverGc ? "Server" : "Workstation")}");
        System.Console.WriteLine($"GC Available Memory: {totalMemoryMb:N0} MB");
        System.Console.WriteLine($"Redis Endpoint: {redisHost}:{redisPort}");
        System.Console.WriteLine($"Track: {track}");
        System.Console.WriteLine(
            $"Workload Unit: 1 shopper = JoinFlashSale + IsInFlashSale + BuildCartItems({minCartSize}..{maxCartSize}) + BrowseHistory + AddToCart + CartReadPhase + SessionAndSalePhase + CommandCoverageMatrix + CheckoutCommit + ReceiptCheck + CartClear + ShopperScopeInvalidation");
        System.Console.WriteLine("Redis Shape: Product=HASH, Cart=LIST, FlashSale=SET, Session=HASH, RecentlyViewed=ZSET, Checkout=STREAM, ReceiptProjection=HASH+SEARCH.");
        System.Console.WriteLine("CommandCoverageMatrix also probes STRING, STREAM, JSON, SEARCH, BLOOM, and TIME-SERIES capabilities when available.");
        System.Console.WriteLine("Workload Shape: 25 products, 5 flash-sales, unique user/session ids per shopper.");
        System.Console.WriteLine(
            $"Harness: warmups={harness.WarmupRuns}, measured-runs={harness.Runs}, alternate-order={harness.AlternateOrder}, deterministic-seed={harness.DeterministicSeed}, cleanup-run-keys={harness.CleanupRunKeys}, timeout={harness.ProviderTimeout.TotalSeconds:N0}s, log-level={harness.BenchmarkLogLevel}, max-degree={(harness.MaxDegreeOfParallelism?.ToString(CultureInfo.InvariantCulture) ?? "auto")}, live-progress={harness.LiveProgressEnabled}, live-interval={harness.LiveProgressInterval.TotalSeconds:N0}s");
        System.Console.WriteLine("Fairness: same shopper workload, same Redis endpoint/auth, same cart-size bounds, same shopper count.");
        System.Console.WriteLine(
            $"ENV|Framework={framework}|OS={os}|Arch={arch}|CpuLogical={cpuLogicalCores}|ServerGC={serverGc}|RedisEndpoint={redisHost}:{redisPort}");
        System.Console.WriteLine(
            $"WORKLOAD|Unit=ShopperFlow|Track={track}|ShopperCount={shopperCount}|CartItemsMin={minCartSize}|CartItemsMax={maxCartSize}|Products=25|FlashSales=5|Warmups={harness.WarmupRuns}|Runs={harness.Runs}|AlternateOrder={harness.AlternateOrder}|Seed={harness.DeterministicSeed}");
    }

    private static async Task<StressTestResult> RunStackExchangeRedisTestAsync(
        IConfiguration configuration,
        string redisHost,
        int redisPort,
        string? redisUsername,
        string redisPassword,
        int shopperCount,
        int minCartSize,
        int maxCartSize,
        GroceryComparisonTrack track,
        string keyPrefix,
        int deterministicSeed,
        HarnessSettings harness)
    {
        var services = new ServiceCollection();
        var checkoutLaneCount = GetIntFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_CHECKOUT_LANES"),
            configuration["GroceryStoreComparison:CheckoutLanes"],
            128);

        // Logging
        services.AddLogging(builder => ConfigureBenchmarkLogging(builder, harness.BenchmarkLogLevel));

        // StackExchange.Redis setup
        var configOptions = new ConfigurationOptions
        {
            EndPoints = { $"{redisHost}:{redisPort}" },
            AbortOnConnectFail = false,
            ConnectTimeout = 5000,
            SyncTimeout = 5000,
            AsyncTimeout = 5000
        };
        if (!string.IsNullOrWhiteSpace(redisPassword))
        {
            configOptions.User = redisUsername;
            configOptions.Password = redisPassword;
        }

        IConnectionMultiplexer? multiplexer = null;
        multiplexer = await ConnectionMultiplexer.ConnectAsync(configOptions).ConfigureAwait(false);
        services.AddSingleton(multiplexer);
        var receiptSearchRuntime = ReceiptSearchRuntimeDescriptor.ForComparison("ser");
        AddSharedComparisonStore(
            services,
            sp => new StackExchangeSuperCenterProvider(
                sp.GetRequiredService<IConnectionMultiplexer>().GetDatabase(),
                keyPrefix,
                receiptSearchRuntime,
                keyPrefix),
            receiptSearchRuntime);
        System.Console.WriteLine($"[SERConfig] KeyPrefix={keyPrefix}");
        System.Console.WriteLine($"[WorkloadConfig] CheckoutLanes={checkoutLaneCount}");

        using var provider = BuildAutofacServiceProvider(services);
        try
        {
            var service = provider.GetRequiredService<IGroceryStoreService>();
            var logger = provider.GetRequiredService<ILogger<GroceryStoreComparisonStressTest>>();

            var providerName = SerProviderName;
            var test = new GroceryStoreComparisonStressTest(
                service,
                logger,
                providerName,
                deterministicSeed,
                harness.MaxDegreeOfParallelism,
                checkoutLaneCount,
                harness.LiveProgressEnabled ? harness.LiveProgressInterval : null,
                CreateLiveProgressSink(track, providerName, harness));
            return await RunWithOptionalTimeoutAsync(
                    ct => test.RunStressTestAsync(shopperCount, minCartSize, maxCartSize, ct),
                    harness.ProviderTimeout,
                    providerName)
                .ConfigureAwait(false);
        }
        finally
        {
            multiplexer.Dispose();
        }
    }

    private static void AddSharedComparisonStore(
        IServiceCollection services,
        Func<IServiceProvider, ISuperCenterStoreProvider> providerFactory,
        ReceiptSearchRuntimeDescriptor receiptSearchRuntime)
    {
        services.AddVapeCacheInvalidation(configure: options =>
        {
            options.Enabled = true;
            options.EnableTagInvalidation = true;
            options.EnableZoneInvalidation = false;
            options.EnableKeyInvalidation = true;
            options.Profile = CacheInvalidationProfile.HighTrafficSite;
        });
        services.AddTagInvalidationPolicy<ShopperScopeInvalidationRequested>(
            static request => [SuperCenterKeySpace.ShopperTag(request.ShopperId)]);
        services.AddCacheInvalidationPolicy<ReceiptFlaggedForReview>(
            _ => new ReceiptFlaggedInvalidationPolicy(receiptSearchRuntime));
        services.AddSingleton<ISuperCenterStoreProvider>(providerFactory);
        services.AddSingleton<IVapeCache, SuperCenterInvalidationVapeCacheBridge>();
        services.AddSingleton<SuperCenterGroceryStoreService>();
        services.AddSingleton<IGroceryStoreService>(sp => sp.GetRequiredService<SuperCenterGroceryStoreService>());
    }

    private static void PrintComparison(StressTestResult vapeCache, StressTestResult stackExchange)
    {
        var throughputRatio = stackExchange.ThroughputShoppersPerSec <= 0
            ? 0m
            : vapeCache.ThroughputShoppersPerSec / stackExchange.ThroughputShoppersPerSec;

        var avgLatencyDeltaPercent = PercentDeltaLowerIsBetter(vapeCache.AverageLatencyMs, stackExchange.AverageLatencyMs);

        var p99LatencyDeltaPercent = PercentDeltaLowerIsBetter(vapeCache.P99LatencyMs, stackExchange.P99LatencyMs);
        var p999LatencyDeltaPercent = PercentDeltaLowerIsBetter(vapeCache.P999LatencyMs, stackExchange.P999LatencyMs);

        System.Console.WriteLine($"Metric                      VapeCache Native   SER                     Winner");
        System.Console.WriteLine("─────────────────────────────────────────────────────────────────────────────");

        PrintMetric("Throughput (shoppers/sec)",
            vapeCache.ThroughputShoppersPerSec,
            stackExchange.ThroughputShoppersPerSec,
            higher: true);

        PrintMetric("Avg Latency (ms)",
            vapeCache.AverageLatencyMs,
            stackExchange.AverageLatencyMs,
            higher: false);

        PrintMetric("p50 Latency (ms)",
            vapeCache.P50LatencyMs,
            stackExchange.P50LatencyMs,
            higher: false);

        PrintMetric("p95 Latency (ms)",
            vapeCache.P95LatencyMs,
            stackExchange.P95LatencyMs,
            higher: false);

        PrintMetric("p99 Latency (ms)",
            vapeCache.P99LatencyMs,
            stackExchange.P99LatencyMs,
            higher: false);

        PrintMetric("p999 Latency (ms)",
            vapeCache.P999LatencyMs,
            stackExchange.P999LatencyMs,
            higher: false);

        var vapeAllocPerShopper = vapeCache.SuccessCount <= 0 ? 0m : vapeCache.AllocatedBytes / (decimal)vapeCache.SuccessCount;
        var serAllocPerShopper = stackExchange.SuccessCount <= 0 ? 0m : stackExchange.AllocatedBytes / (decimal)stackExchange.SuccessCount;
        PrintMetric("Alloc (bytes/shopper)",
            vapeAllocPerShopper,
            serAllocPerShopper,
            higher: false);

        PrintMetric("Gen2 Collections",
            vapeCache.Gen2Collections,
            stackExchange.Gen2Collections,
            higher: false);

        PrintMetric("Shopper Duration (sec)",
            (decimal)vapeCache.ShopperDuration.TotalSeconds,
            (decimal)stackExchange.ShopperDuration.TotalSeconds,
            higher: false);

        PrintMetric("Pre-Cache Duration (ms)",
            (decimal)vapeCache.PreCacheDuration.TotalMilliseconds,
            (decimal)stackExchange.PreCacheDuration.TotalMilliseconds,
            higher: false);

        PrintMetric("End-to-End Duration (sec)",
            (decimal)vapeCache.TotalDuration.TotalSeconds,
            (decimal)stackExchange.TotalDuration.TotalSeconds,
            higher: false);

        PrintMetric("Success Rate (%)",
            (vapeCache.SuccessCount / (decimal)vapeCache.ShopperCount) * 100m,
            (stackExchange.SuccessCount / (decimal)stackExchange.ShopperCount) * 100m,
            higher: true);

        var vapeOpsPerShopper = vapeCache.SuccessCount <= 0 ? 0m : vapeCache.ServiceTotalOps / (decimal)vapeCache.SuccessCount;
        var serOpsPerShopper = stackExchange.SuccessCount <= 0 ? 0m : stackExchange.ServiceTotalOps / (decimal)stackExchange.SuccessCount;
        var vapeCartItemWritesPerShopper = vapeCache.SuccessCount <= 0 ? 0m : vapeCache.ServiceCartItemWriteOps / (decimal)vapeCache.SuccessCount;
        var serCartItemWritesPerShopper = stackExchange.SuccessCount <= 0 ? 0m : stackExchange.ServiceCartItemWriteOps / (decimal)stackExchange.SuccessCount;

        System.Console.WriteLine();
        System.Console.WriteLine("Workload Integrity (provider call accounting):");
        System.Console.WriteLine($"  Service Ops / Shopper: VapeCache Native={vapeOpsPerShopper:N2}, SER={serOpsPerShopper:N2}");
        System.Console.WriteLine($"  Cart Item Writes / Shopper: VapeCache Native={vapeCartItemWritesPerShopper:N2}, SER={serCartItemWritesPerShopper:N2}");
        if (Math.Abs(vapeOpsPerShopper - serOpsPerShopper) > 0.01m ||
            Math.Abs(vapeCartItemWritesPerShopper - serCartItemWritesPerShopper) > 0.01m)
        {
            System.Console.WriteLine("⚠️ Workload parity mismatch detected. Treat throughput comparison as non-authoritative until resolved.");
            System.Console.WriteLine();
        }

        System.Console.WriteLine("═══════════════════════════════════════════════════════════════════════════");
        if (throughputRatio >= 1.0m)
        {
            System.Console.WriteLine($"🏆 VapeCache Native is {throughputRatio:F2}x FASTER than SER");
        }
        else
        {
            var slowerRatio = throughputRatio <= 0 ? 0m : 1.0m / throughputRatio;
            System.Console.WriteLine($"🏆 VapeCache Native is {slowerRatio:F2}x SLOWER than SER");
        }

        var avgLatencyLabel = avgLatencyDeltaPercent >= 0 ? "LOWER" : "HIGHER";
        var p99LatencyLabel = p99LatencyDeltaPercent >= 0 ? "LOWER" : "HIGHER";
        var p999LatencyLabel = p999LatencyDeltaPercent >= 0 ? "LOWER" : "HIGHER";
        System.Console.WriteLine($"📉 VapeCache Native has {Math.Abs(avgLatencyDeltaPercent):F1}% {avgLatencyLabel} average latency");
        System.Console.WriteLine($"🚀 VapeCache Native has {Math.Abs(p99LatencyDeltaPercent):F1}% {p99LatencyLabel} p99 latency");
        System.Console.WriteLine($"🔥 VapeCache Native has {Math.Abs(p999LatencyDeltaPercent):F1}% {p999LatencyLabel} p999 latency");
        System.Console.WriteLine("═══════════════════════════════════════════════════════════════════════════");
    }

    private static void PrintMachineResult(StressTestResult result, GroceryComparisonTrack track)
    {
        var allocPerShopper = result.SuccessCount <= 0 ? 0m : result.AllocatedBytes / (decimal)result.SuccessCount;
        var provider = result.ProviderName.Replace("|", "/", StringComparison.Ordinal);
        System.Console.WriteLine(
            $"RESULT|Track={track}|Provider={provider}|Throughput={result.ThroughputShoppersPerSec:F2}|P50Ms={result.P50LatencyMs:F4}|P95Ms={result.P95LatencyMs:F4}|P99Ms={result.P99LatencyMs:F4}|P999Ms={result.P999LatencyMs:F4}|AllocBytes={result.AllocatedBytes}|AllocBytesPerShopper={allocPerShopper:F2}|Gen0={result.Gen0Collections}|Gen1={result.Gen1Collections}|Gen2={result.Gen2Collections}|Success={result.SuccessCount}|Errors={result.ErrorCount}|ServiceReadOps={result.ServiceReadOps}|ServiceWriteOps={result.ServiceWriteOps}|ServiceAdminOps={result.ServiceAdminOps}|ServiceTotalOps={result.ServiceTotalOps}|ServiceCartItemWrites={result.ServiceCartItemWriteOps}|OptionalSkips={result.ServiceOptionalSkips}");
    }

    private static Action<GroceryStressProgressSnapshot>? CreateLiveProgressSink(
        GroceryComparisonTrack track,
        string providerName,
        HarnessSettings harness)
    {
        if (!harness.LiveProgressEnabled || harness.LiveProgressInterval <= TimeSpan.Zero)
            return null;

        return snapshot =>
        {
            var completionPercent = snapshot.TotalShoppers <= 0
                ? 0d
                : (snapshot.CompletedShoppers * 100d) / snapshot.TotalShoppers;
            var elapsedLabel = snapshot.Elapsed.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);

            System.Console.WriteLine(
                "LIVE|Track={0}|Provider={1}|Elapsed={2}|Completed={3}/{4}|Done={5:F1}%|InFlight={6}|Success={7}|Errors={8}|Thr={9:F1}",
                track,
                providerName.Replace("|", "/", StringComparison.Ordinal),
                elapsedLabel,
                snapshot.CompletedShoppers,
                snapshot.TotalShoppers,
                completionPercent,
                snapshot.InFlightShoppers,
                snapshot.SuccessCount,
                snapshot.ErrorCount,
                snapshot.ThroughputPerSecond);
        };
    }

    private static void PrintMetric(string name, decimal vapeCacheValue, decimal stackExchangeValue, bool higher)
    {
        var winner = higher
            ? (vapeCacheValue > stackExchangeValue ? "VapeCache ✓" : "StackExchange")
            : (vapeCacheValue < stackExchangeValue ? "VapeCache ✓" : "StackExchange");

        var improvement = higher
            ? PercentDeltaHigherIsBetter(vapeCacheValue, stackExchangeValue)
            : PercentDeltaLowerIsBetter(vapeCacheValue, stackExchangeValue);

        var sign = improvement > 0m ? "+" : "";

        System.Console.WriteLine($"{name,-27} {vapeCacheValue,12:N2}   {stackExchangeValue,18:N2}   {winner,-15} ({sign}{improvement:F1}%)");
    }

    private static decimal PercentDeltaHigherIsBetter(decimal candidate, decimal baseline)
    {
        if (baseline <= 0m)
            return 0m;

        return ((candidate - baseline) / baseline) * 100m;
    }

    private static decimal PercentDeltaLowerIsBetter(decimal candidate, decimal baseline)
    {
        if (baseline <= 0m)
            return 0m;

        return ((baseline - candidate) / baseline) * 100m;
    }

    private static int Median(int[] values)
    {
        Array.Sort(values);
        var mid = values.Length / 2;
        if ((values.Length & 1) == 1)
            return values[mid];

        return (int)Math.Round((values[mid - 1] + values[mid]) / 2.0, MidpointRounding.AwayFromZero);
    }

    private static long Median(long[] values)
    {
        Array.Sort(values);
        var mid = values.Length / 2;
        if ((values.Length & 1) == 1)
            return values[mid];

        return (long)Math.Round((values[mid - 1] + values[mid]) / 2.0, MidpointRounding.AwayFromZero);
    }

    private static decimal Median(decimal[] values)
    {
        Array.Sort(values);
        var mid = values.Length / 2;
        if ((values.Length & 1) == 1)
            return values[mid];

        return (values[mid - 1] + values[mid]) / 2m;
    }

    private static void ConfigureBenchmarkLogging(ILoggingBuilder builder, LogLevel minLevel)
    {
        builder.ClearProviders();
        builder.SetMinimumLevel(minLevel);
        builder.AddConsole();
    }

    private static AutofacServiceProvider BuildAutofacServiceProvider(IServiceCollection services)
    {
        var builder = new ContainerBuilder();
        builder.Populate(services);
        return new AutofacServiceProvider(builder.Build());
    }

    private static async Task<StressTestResult> RunWithOptionalTimeoutAsync(
        Func<CancellationToken, Task<StressTestResult>> run,
        TimeSpan timeout,
        string providerName)
    {
        if (timeout <= TimeSpan.Zero)
            return await run(CancellationToken.None).ConfigureAwait(false);

        using var timeoutCts = new CancellationTokenSource();
        var runTask = run(timeoutCts.Token);
        var completed = await Task.WhenAny(runTask, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed == runTask)
            return await runTask.ConfigureAwait(false);

        timeoutCts.Cancel();
        Exception? timeoutInner = null;

        try
        {
            // Allow cooperative cancellation to drain briefly before forcing timeout.
            await runTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            timeoutInner = ex;
        }
        catch (TimeoutException)
        {
            // Timed out waiting for cancellation drain; force fail below.
        }
        catch (Exception ex)
        {
            timeoutInner = ex;
        }

        throw new TimeoutException(
            $"Benchmark provider '{providerName}' exceeded timeout of {timeout.TotalSeconds:N0}s.",
            timeoutInner);
    }

    private static int? GetNullableIntFromSources(string? envValue, string? configValue)
    {
        if (TryParseInt(envValue, allowZero: false, out var parsed))
            return parsed;
        if (TryParseInt(configValue, allowZero: false, out parsed))
            return parsed;
        return null;
    }

    private static LogLevel GetLogLevelFromSources(string? envValue, string? configValue, LogLevel fallback)
    {
        if (TryParseLogLevel(envValue, out var parsed))
            return parsed;
        if (TryParseLogLevel(configValue, out parsed))
            return parsed;
        return fallback;
    }

    private static bool TryParseLogLevel(string? value, out LogLevel level)
    {
        if (Enum.TryParse<LogLevel>(value, ignoreCase: true, out level))
            return true;
        level = default;
        return false;
    }

    private static int GetIntFromSources(string? envValue, string? configValue, int fallback, bool allowZero = false)
    {
        if (TryParseInt(envValue, allowZero, out var parsed))
            return parsed;
        if (TryParseInt(configValue, allowZero, out parsed))
            return parsed;
        return fallback;
    }

    private static double GetDoubleFromSources(string? envValue, string? configValue, double fallback)
    {
        if (TryParseDoubleInvariant(envValue, out var parsed))
            return parsed;
        if (TryParseDoubleInvariant(configValue, out parsed))
            return parsed;
        return fallback;
    }

    private static TimeSpan GetTimeSpanFromSources(string? envValue, string? configValue, TimeSpan fallback)
    {
        if (TryParseTimeSpanOrSeconds(envValue, out var parsed))
            return parsed;
        if (TryParseTimeSpanOrSeconds(configValue, out parsed))
            return parsed;
        return fallback;
    }

    private static bool TryParseInt(string? value, bool allowZero, out int parsed)
    {
        parsed = 0;
        if (!int.TryParse(value, out parsed))
            return false;
        if (parsed < 0)
            return false;
        if (!allowZero && parsed == 0)
            return false;
        return true;
    }

    private static bool TryParseDoubleInvariant(string? value, out double parsed)
        => double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out parsed);

    private static bool TryParseTimeSpanOrSeconds(string? value, out TimeSpan parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0d)
        {
            parsed = TimeSpan.FromSeconds(seconds);
            return true;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out parsed) && parsed > TimeSpan.Zero)
            return true;

        return false;
    }

    private static bool GetBoolFromSources(string? envValue, string? configValue, bool fallback)
    {
        if (bool.TryParse(envValue, out var parsed))
            return parsed;
        if (bool.TryParse(configValue, out parsed))
            return parsed;
        return fallback;
    }

    private static async Task<long> CleanupBenchmarkKeysAsync(string redisHost, int redisPort, string? redisUsername, string redisPassword, string pattern)
    {
        var configOptions = new ConfigurationOptions
        {
            EndPoints = { $"{redisHost}:{redisPort}" },
            AbortOnConnectFail = false,
            ConnectTimeout = 5000,
            SyncTimeout = 5000,
            AsyncTimeout = 5000
        };
        if (!string.IsNullOrWhiteSpace(redisPassword))
        {
            configOptions.User = redisUsername;
            configOptions.Password = redisPassword;
        }

        using var multiplexer = await ConnectionMultiplexer.ConnectAsync(configOptions).ConfigureAwait(false);
        var endpoints = multiplexer.GetEndPoints();
        if (endpoints.Length == 0)
            return 0;

        var server = multiplexer.GetServer(endpoints[0]);
        if (!server.IsConnected)
            return 0;

        var db = multiplexer.GetDatabase();
        var batch = new List<RedisKey>(1024);
        long deleted = 0;
        foreach (var key in server.Keys(db.Database, pattern, pageSize: 1000))
        {
            batch.Add(key);
            if (batch.Count < 1024)
                continue;

            deleted += await DeleteBatchAsync(db, batch).ConfigureAwait(false);
            batch.Clear();
        }

        if (batch.Count > 0)
            deleted += await DeleteBatchAsync(db, batch).ConfigureAwait(false);

        return deleted;
    }

    private static async Task<long> DeleteBatchAsync(IDatabase db, List<RedisKey> batch)
    {
        // Prefer UNLINK to avoid synchronous key free stalls between benchmark iterations.
        var args = new object[batch.Count];
        for (var i = 0; i < batch.Count; i++)
            args[i] = batch[i];

        try
        {
            var reply = await db.ExecuteAsync("UNLINK", args).ConfigureAwait(false);
            if (long.TryParse(reply.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        catch (RedisServerException ex) when (ex.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase))
        {
            // Redis < 4.0 fallback.
        }
        catch (RedisServerException ex) when (ex.Message.Contains("NOPERM", StringComparison.OrdinalIgnoreCase))
        {
            // ACL does not allow UNLINK; fallback to DEL.
        }

        return await db.KeyDeleteAsync(batch.ToArray()).ConfigureAwait(false);
    }

    private static async Task CleanupRunKeysSafelyAsync(string redisHost, int redisPort, string? redisUsername, string redisPassword, string keyPrefix)
    {
        try
        {
            var pattern = string.Concat(keyPrefix, "*");
            var deleted = await CleanupBenchmarkKeysAsync(redisHost, redisPort, redisUsername, redisPassword, pattern).ConfigureAwait(false);
            if (deleted > 0)
                System.Console.WriteLine($"[BenchCleanup] Removed {deleted:N0} keys for prefix {keyPrefix}");
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"[BenchCleanup] Warning: failed to remove keys for prefix {keyPrefix}. {ex.Message}");
        }
    }

    private static GroceryComparisonTrack GetTrackFromSources(string? envValue, string? configValue, GroceryComparisonTrack fallback)
    {
        var resolved = !string.IsNullOrWhiteSpace(envValue) ? envValue : configValue;
        if (string.IsNullOrWhiteSpace(resolved))
            return fallback;

        return resolved.Trim().ToLowerInvariant() switch
        {
            "apples" => GroceryComparisonTrack.ApplesToApples,
            "apples-to-apples" => GroceryComparisonTrack.ApplesToApples,
            "parity" => GroceryComparisonTrack.ApplesToApples,
            "optimized" => GroceryComparisonTrack.OptimizedProductPath,
            "optimizedproductpath" => GroceryComparisonTrack.OptimizedProductPath,
            "product" => GroceryComparisonTrack.OptimizedProductPath,
            "both" => GroceryComparisonTrack.Both,
            _ => fallback
        };
    }

    private static RedisTransportProfile GetTransportProfileFromSources(string? envValue, string? configValue, RedisTransportProfile fallback)
    {
        var resolved = !string.IsNullOrWhiteSpace(envValue) ? envValue : configValue;
        if (string.IsNullOrWhiteSpace(resolved))
            return fallback;

        return resolved.Trim().ToLowerInvariant() switch
        {
            "fulltilt" => RedisTransportProfile.FullTilt,
            "full-tilt" => RedisTransportProfile.FullTilt,
            "balanced" => RedisTransportProfile.Balanced,
            "lowlatency" => RedisTransportProfile.LowLatency,
            "low-latency" => RedisTransportProfile.LowLatency,
            "latency" => RedisTransportProfile.LowLatency,
            "custom" => RedisTransportProfile.Custom,
            _ => fallback
        };
    }

    private static VapeExecutorMode GetVapeExecutorModeFromSources(string? envValue, string? configValue, VapeExecutorMode fallback)
    {
        var resolved = !string.IsNullOrWhiteSpace(envValue) ? envValue : configValue;
        if (string.IsNullOrWhiteSpace(resolved))
            return fallback;

        return resolved.Trim().ToLowerInvariant() switch
        {
            "raw" => VapeExecutorMode.Raw,
            "hybrid" => VapeExecutorMode.HybridFailover,
            "hybridfailover" => VapeExecutorMode.HybridFailover,
            "hybrid-failover" => VapeExecutorMode.HybridFailover,
            "resilient" => VapeExecutorMode.HybridFailover,
            _ => fallback
        };
    }
}
