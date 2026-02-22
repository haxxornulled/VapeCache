using Microsoft.Extensions.DependencyInjection;
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
    public static async Task RunComparisonAsync(string redisHost, string redisPassword, int shopperCount = 10_000, int maxCartSize = 35)
    {
        System.Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        System.Console.WriteLine("║       VapeCache vs StackExchange.Redis Showdown             ║");
        System.Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        System.Console.WriteLine();

        // Run VapeCache test
        var vapeCacheResult = await RunVapeCacheTestAsync(redisHost, redisPassword, shopperCount, maxCartSize);

        System.Console.WriteLine();
        System.Console.WriteLine("════════════════════════════════════════════════════════════════");
        System.Console.WriteLine();

        // Run StackExchange.Redis test
        var stackExchangeResult = await RunStackExchangeRedisTestAsync(redisHost, redisPassword, shopperCount, maxCartSize);

        System.Console.WriteLine();
        System.Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        System.Console.WriteLine("║                    COMPARISON RESULTS                        ║");
        System.Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        System.Console.WriteLine();

        PrintComparison(vapeCacheResult, stackExchangeResult);
    }

    private static async Task<StressTestResult> RunVapeCacheTestAsync(string redisHost, string redisPassword, int shopperCount, int maxCartSize)
    {
        var services = new ServiceCollection();
        var muxConnections = GetIntFromEnv("VAPECACHE_BENCH_MUX_CONNECTIONS", 8);
        var muxInFlight = GetIntFromEnv("VAPECACHE_BENCH_MUX_INFLIGHT", 8192);
        var muxResponseTimeoutMs = GetIntFromEnv("VAPECACHE_BENCH_MUX_RESPONSE_TIMEOUT_MS", 0);
        var muxCoalesce = GetBoolFromEnv("VAPECACHE_BENCH_MUX_COALESCE", true);

        System.Console.WriteLine(
            $"[VapeConfig] Mux.Connections={muxConnections}, Mux.MaxInFlight={muxInFlight}, Mux.Coalesce={muxCoalesce}, Mux.ResponseTimeoutMs={muxResponseTimeoutMs}");

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
        services.AddSingleton<IRedisCommandExecutor>(CreateRawRedisExecutor);
        services.AddSingleton<IGroceryStoreService, VapeCacheRawGroceryStoreService>();

        var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IGroceryStoreService>();
        var logger = provider.GetRequiredService<ILogger<GroceryStoreComparisonStressTest>>();

        var test = new GroceryStoreComparisonStressTest(service, logger, "VapeCache");
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
            User = "admin",
            Password = redisPassword,
            AbortOnConnectFail = false,
            ConnectTimeout = 5000,
            SyncTimeout = 5000,
            AsyncTimeout = 5000
        };

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
            ? 0
            : vapeCache.ThroughputShoppersPerSec / stackExchange.ThroughputShoppersPerSec;

        var avgLatencyDeltaPercent = stackExchange.AverageLatencyMs <= 0
            ? 0
            : ((stackExchange.AverageLatencyMs - vapeCache.AverageLatencyMs) / stackExchange.AverageLatencyMs) * 100;

        var p99LatencyDeltaPercent = stackExchange.P99LatencyMs <= 0
            ? 0
            : ((stackExchange.P99LatencyMs - vapeCache.P99LatencyMs) / stackExchange.P99LatencyMs) * 100;

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

        PrintMetric("Total Duration (sec)",
            vapeCache.TotalDuration.TotalSeconds,
            stackExchange.TotalDuration.TotalSeconds,
            higher: false);

        PrintMetric("Success Rate (%)",
            (vapeCache.SuccessCount / (double)vapeCache.ShopperCount) * 100,
            (stackExchange.SuccessCount / (double)stackExchange.ShopperCount) * 100,
            higher: true);

        System.Console.WriteLine();
        System.Console.WriteLine("═══════════════════════════════════════════════════════════════════════════");
        if (throughputRatio >= 1.0)
        {
            System.Console.WriteLine($"🏆 VapeCache is {throughputRatio:F2}x FASTER than StackExchange.Redis");
        }
        else
        {
            var slowerRatio = throughputRatio <= 0 ? 0 : 1.0 / throughputRatio;
            System.Console.WriteLine($"🏆 VapeCache is {slowerRatio:F2}x SLOWER than StackExchange.Redis");
        }

        var avgLatencyLabel = avgLatencyDeltaPercent >= 0 ? "LOWER" : "HIGHER";
        var p99LatencyLabel = p99LatencyDeltaPercent >= 0 ? "LOWER" : "HIGHER";
        System.Console.WriteLine($"📉 VapeCache has {Math.Abs(avgLatencyDeltaPercent):F1}% {avgLatencyLabel} average latency");
        System.Console.WriteLine($"🚀 VapeCache has {Math.Abs(p99LatencyDeltaPercent):F1}% {p99LatencyLabel} p99 latency");
        System.Console.WriteLine("═══════════════════════════════════════════════════════════════════════════");
    }

    private static void PrintMetric(string name, double vapeCacheValue, double stackExchangeValue, bool higher)
    {
        var winner = higher
            ? (vapeCacheValue > stackExchangeValue ? "VapeCache ✓" : "StackExchange")
            : (vapeCacheValue < stackExchangeValue ? "VapeCache ✓" : "StackExchange");

        var improvement = higher
            ? ((vapeCacheValue - stackExchangeValue) / stackExchangeValue) * 100
            : ((stackExchangeValue - vapeCacheValue) / stackExchangeValue) * 100;

        var sign = improvement > 0 ? "+" : "";

        System.Console.WriteLine($"{name,-27} {vapeCacheValue,12:N2}   {stackExchangeValue,18:N2}   {winner,-15} ({sign}{improvement:F1}%)");
    }

    private static IRedisCommandExecutor CreateRawRedisExecutor(IServiceProvider services)
    {
        var executorType = typeof(RedisConnectionRegistration).Assembly.GetType(
            "VapeCache.Infrastructure.Connections.RedisCommandExecutor",
            throwOnError: true)!;

        var ctor = executorType.GetConstructor(new[]
        {
            typeof(IRedisConnectionFactory),
            typeof(IOptionsMonitor<RedisMultiplexerOptions>),
            typeof(IOptionsMonitor<RedisConnectionOptions>)
        });

        if (ctor is null)
        {
            throw new InvalidOperationException("Unable to resolve RedisCommandExecutor constructor for raw benchmark mode.");
        }

        var instance = ctor.Invoke(new object[]
        {
            services.GetRequiredService<IRedisConnectionFactory>(),
            services.GetRequiredService<IOptionsMonitor<RedisMultiplexerOptions>>(),
            services.GetRequiredService<IOptionsMonitor<RedisConnectionOptions>>()
        });

        return (IRedisCommandExecutor)instance;
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

    private static int GetIntFromEnv(string name, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private static bool GetBoolFromEnv(string name, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }
}
