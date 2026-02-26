using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

    /// <summary>
    /// Runs value.
    /// </summary>
    public static async Task RunComparisonAsync(
        IConfiguration configuration,
        string redisHost,
        string redisPassword,
        int shopperCount = 10_000,
        int maxCartSize = 35)
    {
        System.Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        System.Console.WriteLine("║       VapeCache vs StackExchange.Redis Showdown             ║");
        System.Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        System.Console.WriteLine();

        var benchTrack = GetTrackFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_TRACK"),
            configuration["GroceryStoreComparison:BenchTrack"],
            GroceryComparisonTrack.OptimizedProductPath);
        System.Console.WriteLine($"[BenchTrack] {benchTrack}");
        System.Console.WriteLine();

        if (benchTrack == GroceryComparisonTrack.Both)
        {
            var vapeParityResult = await RunVapeCacheTestAsync(configuration, redisHost, redisPassword, shopperCount, maxCartSize, GroceryComparisonTrack.ApplesToApples);
            PrintMachineResult(vapeParityResult);
            System.Console.WriteLine();
            System.Console.WriteLine("════════════════════════════════════════════════════════════════");
            System.Console.WriteLine();

            var vapeOptimizedResult = await RunVapeCacheTestAsync(configuration, redisHost, redisPassword, shopperCount, maxCartSize, GroceryComparisonTrack.OptimizedProductPath);
            PrintMachineResult(vapeOptimizedResult);
            System.Console.WriteLine();
            System.Console.WriteLine("════════════════════════════════════════════════════════════════");
            System.Console.WriteLine();

            var stackExchangeResult = await RunStackExchangeRedisTestAsync(redisHost, redisPassword, shopperCount, maxCartSize);
            PrintMachineResult(stackExchangeResult);
            System.Console.WriteLine();
            System.Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            System.Console.WriteLine("║                    COMPARISON RESULTS                        ║");
            System.Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            System.Console.WriteLine();

            System.Console.WriteLine("Track: ApplesToApples");
            PrintComparison(vapeParityResult, stackExchangeResult);
            System.Console.WriteLine();
            System.Console.WriteLine("Track: OptimizedProductPath");
            PrintComparison(vapeOptimizedResult, stackExchangeResult);
            return;
        }

        var vapeCacheResult = await RunVapeCacheTestAsync(configuration, redisHost, redisPassword, shopperCount, maxCartSize, benchTrack);
        PrintMachineResult(vapeCacheResult);
        System.Console.WriteLine();
        System.Console.WriteLine("════════════════════════════════════════════════════════════════");
        System.Console.WriteLine();

        var stackExchangeResultSingle = await RunStackExchangeRedisTestAsync(redisHost, redisPassword, shopperCount, maxCartSize);
        PrintMachineResult(stackExchangeResultSingle);
        System.Console.WriteLine();
        System.Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        System.Console.WriteLine("║                    COMPARISON RESULTS                        ║");
        System.Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        System.Console.WriteLine();

        PrintComparison(vapeCacheResult, stackExchangeResultSingle);
    }

    private static async Task<StressTestResult> RunVapeCacheTestAsync(
        IConfiguration configuration,
        string redisHost,
        string redisPassword,
        int shopperCount,
        int maxCartSize,
        GroceryComparisonTrack track)
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
        var muxProfile = GetTransportProfileFromSources(
            Environment.GetEnvironmentVariable("VAPECACHE_BENCH_MUX_PROFILE"),
            configuration["GroceryStoreComparison:MuxProfile"],
            RedisTransportProfile.FullTilt);

        System.Console.WriteLine(
            $"[VapeConfig] Mux.Profile={muxProfile}, Mux.Connections={muxConnections}, Mux.MaxInFlight={muxInFlight}, Mux.Coalesce={muxCoalesce}, Mux.ResponseTimeoutMs={muxResponseTimeoutMs}");

        // Logging
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddConsole();
        });

        // VapeCache setup
        services.AddOptions<VapeCache.Abstractions.Connections.RedisConnectionOptions>()
            .Configure(options =>
            {
                typeof(VapeCache.Abstractions.Connections.RedisConnectionOptions)
                    .GetProperty(nameof(VapeCache.Abstractions.Connections.RedisConnectionOptions.Host))!
                    .SetValue(options, redisHost);
                typeof(VapeCache.Abstractions.Connections.RedisConnectionOptions)
                    .GetProperty(nameof(VapeCache.Abstractions.Connections.RedisConnectionOptions.Port))!
                    .SetValue(options, 6379);
                typeof(VapeCache.Abstractions.Connections.RedisConnectionOptions)
                    .GetProperty(nameof(VapeCache.Abstractions.Connections.RedisConnectionOptions.Username))!
                    .SetValue(options, "admin");
                typeof(VapeCache.Abstractions.Connections.RedisConnectionOptions)
                    .GetProperty(nameof(VapeCache.Abstractions.Connections.RedisConnectionOptions.Password))!
                    .SetValue(options, redisPassword);
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
            services.AddSingleton<IGroceryStoreService, VapeCacheRawParityGroceryStoreService>();
        else
            services.AddSingleton<IGroceryStoreService, VapeCacheRawGroceryStoreService>();

        var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IGroceryStoreService>();
        var logger = provider.GetRequiredService<ILogger<GroceryStoreComparisonStressTest>>();

        var providerName = track == GroceryComparisonTrack.ApplesToApples
            ? "VapeCache (ApplesToApples)"
            : "VapeCache (OptimizedProductPath)";
        var test = new GroceryStoreComparisonStressTest(service, logger, providerName);
        return await test.RunStressTestAsync(shopperCount, maxCartSize);
    }

    private static async Task<StressTestResult> RunStackExchangeRedisTestAsync(string redisHost, string redisPassword, int shopperCount, int maxCartSize)
    {
        var services = new ServiceCollection();

        // Logging
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddConsole();
        });

        // StackExchange.Redis setup
        var configOptions = new ConfigurationOptions
        {
            EndPoints = { $"{redisHost}:6379" },
            AbortOnConnectFail = false,
            ConnectTimeout = 5000,
            SyncTimeout = 5000,
            AsyncTimeout = 5000
        };
        if (!string.IsNullOrWhiteSpace(redisPassword))
        {
            configOptions.User = "admin";
            configOptions.Password = redisPassword;
        }

        var multiplexer = await ConnectionMultiplexer.ConnectAsync(configOptions);
        services.AddSingleton<IConnectionMultiplexer>(multiplexer);
        services.AddSingleton<IGroceryStoreService, StackExchangeRedisGroceryStoreService>();

        var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IGroceryStoreService>();
        var logger = provider.GetRequiredService<ILogger<GroceryStoreComparisonStressTest>>();

        var test = new GroceryStoreComparisonStressTest(service, logger, "StackExchange.Redis");
        return await test.RunStressTestAsync(shopperCount, maxCartSize);
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

        PrintMetric("Total Duration (sec)",
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
