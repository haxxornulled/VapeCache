using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Globalization;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;
using StackExchange.Redis;

namespace VapeCache.Console.GroceryStore;

/// <summary>
/// Runs head-to-head comparison between VapeCache and StackExchange.Redis.
/// Same workload, different providers - let's see who wins!
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
        VapeCache,
        StackExchangeRedis
    }

    private readonly record struct HarnessSettings(
        int Runs,
        int WarmupRuns,
        bool AlternateOrder,
        int DeterministicSeed,
        bool CleanupRunKeys,
        TimeSpan ProviderTimeout,
        LogLevel BenchmarkLogLevel,
        int? MaxDegreeOfParallelism);

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
        int maxCartSize = 35)
    {
        System.Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        System.Console.WriteLine("║       VapeCache vs StackExchange.Redis Showdown             ║");
        System.Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        System.Console.WriteLine();

        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        System.Console.WriteLine($"[RunId] {runId}");
        System.Console.WriteLine();

        var benchTrack = GetTrackFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_TRACK"),
            configuration["GroceryStoreComparison:BenchTrack"],
            GroceryComparisonTrack.OptimizedProductPath);
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
                configuration["GroceryStoreComparison:MaxDegreeOfParallelism"]));

        if (cleanupBenchKeys)
        {
            var deleted = await CleanupBenchmarkKeysAsync(redisHost, redisPort, redisUsername, redisPassword, "cmp:*").ConfigureAwait(false);
            System.Console.WriteLine($"[BenchCleanup] Deleted {deleted:N0} stale comparison keys (pattern: cmp:*).");
            System.Console.WriteLine();
        }

        System.Console.WriteLine($"[BenchTrack] {benchTrack}");
        System.Console.WriteLine("Track Definitions:");
        System.Console.WriteLine("  ApplesToApples: command/payload parity with StackExchange.Redis (JSON cart/session payloads).");
        System.Console.WriteLine("  OptimizedProductPath: VapeCache optimized cart/session storage path.");
        PrintBenchmarkHeader(redisHost, redisPort, shopperCount, maxCartSize, benchTrack, harness);
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
                    if (provider == ComparisonProvider.VapeCache)
                    {
                        var prefix = $"cmp:{runId}:{track}:vape:i{iteration:D2}:";
                        iterationPrefixes.Add(prefix);
                        var result = await RunVapeCacheTestAsync(
                            configuration,
                            redisHost,
                            redisPort,
                            redisUsername,
                            redisPassword,
                            shopperCount,
                            maxCartSize,
                            track,
                            prefix,
                            deterministicSeed,
                            harness).ConfigureAwait(false);
                        PrintMachineResult(result);
                        if (!warmup)
                            measuredVape.Add(result);
                    }
                    else
                    {
                        var prefix = $"cmp:{runId}:{track}:ser:i{iteration:D2}:";
                        iterationPrefixes.Add(prefix);
                        var result = await RunStackExchangeRedisTestAsync(
                            redisHost,
                            redisPort,
                            redisUsername,
                            redisPassword,
                            shopperCount,
                            maxCartSize,
                            prefix,
                            deterministicSeed,
                            harness).ConfigureAwait(false);
                        PrintMachineResult(result);
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

        var vapeProviderName = track == GroceryComparisonTrack.ApplesToApples
            ? "VapeCache (ApplesToApples)"
            : "VapeCache (OptimizedProductPath)";
        var serProviderName = "StackExchange.Redis";

        var vapeAggregated = AggregateMedianResult(vapeProviderName, shopperCount, measuredVape);
        var serAggregated = AggregateMedianResult(serProviderName, shopperCount, measuredSer);
        return (vapeAggregated, serAggregated);
    }

    private static ComparisonProvider[] GetExecutionOrder(bool alternateOrder, int iteration)
    {
        if (!alternateOrder || (iteration % 2) == 0)
            return [ComparisonProvider.VapeCache, ComparisonProvider.StackExchangeRedis];

        return [ComparisonProvider.StackExchangeRedis, ComparisonProvider.VapeCache];
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
            Gen2Collections: Median(results.Select(static r => r.Gen2Collections).ToArray()));
    }

    private static async Task<StressTestResult> RunVapeCacheTestAsync(
        IConfiguration configuration,
        string redisHost,
        int redisPort,
        string? redisUsername,
        string redisPassword,
        int shopperCount,
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
        var muxProfile = GetTransportProfileFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_MUX_PROFILE"),
            configuration["GroceryStoreComparison:MuxProfile"],
            RedisTransportProfile.FullTilt);

        System.Console.WriteLine(
            $"[VapeConfig] Mux.Profile={muxProfile}, Mux.Connections={muxConnections}, Mux.MaxInFlight={muxInFlight}, Mux.Coalesce={muxCoalesce}, Mux.AdaptiveCoalesce={muxAdaptiveCoalescing}, Mux.SocketReader={muxSocketRespReader}, Mux.DedicatedWorkers={muxDedicatedLaneWorkers}, Mux.ResponseTimeoutMs={muxResponseTimeoutMs}");
        System.Console.WriteLine($"[VapeConfig] KeyPrefix={keyPrefix}");

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
        });
        services.AddOptions<RedisCircuitBreakerOptions>().Configure(options =>
        {
            typeof(RedisCircuitBreakerOptions)
                .GetProperty(nameof(RedisCircuitBreakerOptions.Enabled))!
                .SetValue(options, false);
        });
        services.AddOptions<CacheStampedeOptions>().Configure(options =>
        {
            typeof(CacheStampedeOptions)
                .GetProperty(nameof(CacheStampedeOptions.Enabled))!
                .SetValue(options, false);
        });

        RegisterInternalBenchmarkServices(services);
        services.AddVapecacheRedisConnections();
        services.AddSingleton<IRedisCommandExecutor>(_ =>
        {
            var executorType = typeof(RedisConnectionRegistration).Assembly.GetType(
                "VapeCache.Infrastructure.Connections.RedisCommandExecutor",
                throwOnError: true)!;
            return (IRedisCommandExecutor)ActivatorUtilities.CreateInstance(_, executorType);
        });
        if (track == GroceryComparisonTrack.ApplesToApples)
            services.AddSingleton<IGroceryStoreService>(sp =>
                new VapeCacheRawParityGroceryStoreService(
                    sp.GetRequiredService<IRedisCommandExecutor>(),
                    keyPrefix));
        else
            services.AddSingleton<IGroceryStoreService>(sp =>
                new VapeCacheRawGroceryStoreService(
                    sp.GetRequiredService<IRedisCommandExecutor>(),
                    keyPrefix,
                    optimizedCleanupOnly: true,
                    useLocalFlashSaleCountCache: true));

        var provider = services.BuildServiceProvider();
        try
        {
            var service = provider.GetRequiredService<IGroceryStoreService>();
            var logger = provider.GetRequiredService<ILogger<GroceryStoreComparisonStressTest>>();

            var providerName = track == GroceryComparisonTrack.ApplesToApples
                ? "VapeCache (ApplesToApples)"
                : "VapeCache (OptimizedProductPath)";
            var test = new GroceryStoreComparisonStressTest(
                service,
                logger,
                providerName,
                deterministicSeed,
                harness.MaxDegreeOfParallelism);
            return await RunWithOptionalTimeoutAsync(
                    () => test.RunStressTestAsync(shopperCount, maxCartSize),
                    harness.ProviderTimeout,
                    providerName)
                .ConfigureAwait(false);
        }
        finally
        {
            await provider.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void PrintBenchmarkHeader(
        string redisHost,
        int redisPort,
        int shopperCount,
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
            $"Workload Unit: 1 shopper = JoinFlashSale + IsInFlashSale + BuildCartItems(15..{maxCartSize}) + AddToCart + CartReadPhase + SessionAndSalePhase + ClearCart");
        System.Console.WriteLine("Workload Shape: 25 products, 5 flash-sales, unique user/session ids per shopper.");
        System.Console.WriteLine(
            $"Harness: warmups={harness.WarmupRuns}, measured-runs={harness.Runs}, alternate-order={harness.AlternateOrder}, deterministic-seed={harness.DeterministicSeed}, cleanup-run-keys={harness.CleanupRunKeys}, timeout={harness.ProviderTimeout.TotalSeconds:N0}s, log-level={harness.BenchmarkLogLevel}, max-degree={(harness.MaxDegreeOfParallelism?.ToString(CultureInfo.InvariantCulture) ?? "auto")}");
        System.Console.WriteLine("Fairness: same shopper workload, same Redis endpoint/auth, same cart-size bounds, same shopper count.");
        System.Console.WriteLine(
            $"ENV|Framework={framework}|OS={os}|Arch={arch}|CpuLogical={cpuLogicalCores}|ServerGC={serverGc}|RedisEndpoint={redisHost}:{redisPort}");
        System.Console.WriteLine(
            $"WORKLOAD|Unit=ShopperFlow|Track={track}|ShopperCount={shopperCount}|CartItemsMin=15|CartItemsMax={maxCartSize}|Products=25|FlashSales=5|Warmups={harness.WarmupRuns}|Runs={harness.Runs}|AlternateOrder={harness.AlternateOrder}|Seed={harness.DeterministicSeed}");
    }

    private static async Task<StressTestResult> RunStackExchangeRedisTestAsync(
        string redisHost,
        int redisPort,
        string? redisUsername,
        string redisPassword,
        int shopperCount,
        int maxCartSize,
        string keyPrefix,
        int deterministicSeed,
        HarnessSettings harness)
    {
        var services = new ServiceCollection();

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

        var multiplexer = await ConnectionMultiplexer.ConnectAsync(configOptions);
        services.AddSingleton<IConnectionMultiplexer>(multiplexer);
        services.AddSingleton<IGroceryStoreService>(sp =>
            new StackExchangeRedisGroceryStoreService(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                keyPrefix));
        System.Console.WriteLine($"[SERConfig] KeyPrefix={keyPrefix}");

        var provider = services.BuildServiceProvider();
        try
        {
            var service = provider.GetRequiredService<IGroceryStoreService>();
            var logger = provider.GetRequiredService<ILogger<GroceryStoreComparisonStressTest>>();

            var providerName = "StackExchange.Redis";
            var test = new GroceryStoreComparisonStressTest(
                service,
                logger,
                providerName,
                deterministicSeed,
                harness.MaxDegreeOfParallelism);
            return await RunWithOptionalTimeoutAsync(
                    () => test.RunStressTestAsync(shopperCount, maxCartSize),
                    harness.ProviderTimeout,
                    providerName)
                .ConfigureAwait(false);
        }
        finally
        {
            await provider.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void PrintComparison(StressTestResult vapeCache, StressTestResult stackExchange)
    {
        var throughputRatio = stackExchange.ThroughputShoppersPerSec <= 0
            ? 0m
            : vapeCache.ThroughputShoppersPerSec / stackExchange.ThroughputShoppersPerSec;

        var avgLatencyDeltaPercent = PercentDeltaLowerIsBetter(vapeCache.AverageLatencyMs, stackExchange.AverageLatencyMs);

        var p99LatencyDeltaPercent = PercentDeltaLowerIsBetter(vapeCache.P99LatencyMs, stackExchange.P99LatencyMs);
        var p999LatencyDeltaPercent = PercentDeltaLowerIsBetter(vapeCache.P999LatencyMs, stackExchange.P999LatencyMs);

        System.Console.WriteLine($"Metric                      VapeCache          StackExchange.Redis     Winner");
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

        System.Console.WriteLine();
        System.Console.WriteLine("═══════════════════════════════════════════════════════════════════════════");
        if (throughputRatio >= 1.0m)
        {
            System.Console.WriteLine($"🏆 VapeCache is {throughputRatio:F2}x FASTER than StackExchange.Redis");
        }
        else
        {
            var slowerRatio = throughputRatio <= 0 ? 0m : 1.0m / throughputRatio;
            System.Console.WriteLine($"🏆 VapeCache is {slowerRatio:F2}x SLOWER than StackExchange.Redis");
        }

        var avgLatencyLabel = avgLatencyDeltaPercent >= 0 ? "LOWER" : "HIGHER";
        var p99LatencyLabel = p99LatencyDeltaPercent >= 0 ? "LOWER" : "HIGHER";
        var p999LatencyLabel = p999LatencyDeltaPercent >= 0 ? "LOWER" : "HIGHER";
        System.Console.WriteLine($"📉 VapeCache has {Math.Abs(avgLatencyDeltaPercent):F1}% {avgLatencyLabel} average latency");
        System.Console.WriteLine($"🚀 VapeCache has {Math.Abs(p99LatencyDeltaPercent):F1}% {p99LatencyLabel} p99 latency");
        System.Console.WriteLine($"🔥 VapeCache has {Math.Abs(p999LatencyDeltaPercent):F1}% {p999LatencyLabel} p999 latency");
        System.Console.WriteLine("═══════════════════════════════════════════════════════════════════════════");
    }

    private static void PrintMachineResult(StressTestResult result)
    {
        var allocPerShopper = result.SuccessCount <= 0 ? 0m : result.AllocatedBytes / (decimal)result.SuccessCount;
        var provider = result.ProviderName.Replace("|", "/", StringComparison.Ordinal);
        System.Console.WriteLine(
            $"RESULT|Provider={provider}|Throughput={result.ThroughputShoppersPerSec:F2}|P95Ms={result.P95LatencyMs:F4}|P99Ms={result.P99LatencyMs:F4}|P999Ms={result.P999LatencyMs:F4}|AllocBytes={result.AllocatedBytes}|AllocBytesPerShopper={allocPerShopper:F2}|Gen0={result.Gen0Collections}|Gen1={result.Gen1Collections}|Gen2={result.Gen2Collections}|Success={result.SuccessCount}|Errors={result.ErrorCount}");
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

    private static void RegisterInternalBenchmarkServices(IServiceCollection services)
    {
        var infrastructureAssembly = typeof(RedisConnectionRegistration).Assembly;
        var cacheStatsRegistryType = infrastructureAssembly.GetType(
            "VapeCache.Infrastructure.Caching.CacheStatsRegistry",
            throwOnError: true)!;

        services.AddSingleton(
            cacheStatsRegistryType,
            _ => Activator.CreateInstance(cacheStatsRegistryType)!);
    }

    private static void ConfigureBenchmarkLogging(ILoggingBuilder builder, LogLevel minLevel)
    {
        builder.ClearProviders();
        builder.SetMinimumLevel(minLevel);
        builder.AddConsole();
    }

    private static async Task<StressTestResult> RunWithOptionalTimeoutAsync(
        Func<Task<StressTestResult>> run,
        TimeSpan timeout,
        string providerName)
    {
        if (timeout <= TimeSpan.Zero)
            return await run().ConfigureAwait(false);

        try
        {
            return await run().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"Benchmark provider '{providerName}' exceeded timeout of {timeout.TotalSeconds:N0}s.",
                ex);
        }
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
}
