using Microsoft.Extensions.Configuration;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Console.GroceryStore;

/// <summary>
/// Interactive menu for running VapeCache vs StackExchange.Redis comparison.
/// </summary>
public static class MenuRunner
{
    private readonly record struct RedisComparisonSettings(
        string Host,
        int Port,
        string? Username,
        string Password,
        string Source);

    /// <summary>
    /// Runs value.
    /// </summary>
    public static async Task RunAsync(IConfiguration configuration)
    {
        try { System.Console.Clear(); } catch { /* Ignore - may not have console */ }
        System.Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        System.Console.WriteLine("║          VapeCache Grocery Store Performance Test           ║");
        System.Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        System.Console.WriteLine();

        // Resolve Redis endpoint/auth from the same source used by the host runtime.
        var redis = ResolveRedisSettings(configuration);
        var redisHost = redis.Host;
        var redisPort = redis.Port;
        var redisUsername = redis.Username;
        var redisPassword = redis.Password;

        var authMode = string.IsNullOrWhiteSpace(redisPassword)
            ? "none"
            : (string.IsNullOrWhiteSpace(redisUsername) ? "password-only" : "acl");
        System.Console.WriteLine($"Redis Endpoint: {redisHost}:{redisPort}");
        System.Console.WriteLine($"Redis Auth: {authMode}");
        System.Console.WriteLine($"Redis Source: {redis.Source}");
        System.Console.WriteLine();

        // Auto-run with default settings when running in non-interactive mode
        var autoRun = Environment.GetEnvironmentVariable("VAPECACHE_RUN_COMPARISON")?.ToLowerInvariant() == "true";
        int shopperCount = 10_000;
        if (int.TryParse(Environment.GetEnvironmentVariable("VAPECACHE_BENCH_SHOPPERS"), out var envShoppers) && envShoppers > 0)
            shopperCount = envShoppers;
        var maxCartSize = 35;
        if (int.TryParse(Environment.GetEnvironmentVariable("VAPECACHE_MAX_CART_SIZE"), out var envMaxCartSize) && envMaxCartSize > 0)
            maxCartSize = envMaxCartSize;

        if (!autoRun)
        {
            System.Console.WriteLine("Enter shopper count to run comparison:");
            System.Console.WriteLine();
            System.Console.WriteLine("  Presets: 10000, 50000, 100000");
            System.Console.WriteLine("  Enter 0 to exit");
            System.Console.WriteLine();
            System.Console.Write("Shopper count: ");

            shopperCount = GetCustomShopperCount();
        }

        if (shopperCount > 0)
        {
            System.Console.WriteLine();
            System.Console.WriteLine($"Starting comparison with {shopperCount:N0} shoppers (max cart size: {maxCartSize})...");
            System.Console.WriteLine("This may take a few minutes. Please wait...");
            System.Console.WriteLine();

            try
            {
                await ComparisonRunner.RunComparisonAsync(
                    configuration,
                    redisHost,
                    redisPort,
                    redisUsername,
                    redisPassword,
                    shopperCount,
                    maxCartSize);
            }
            catch (Exception ex)
            {
                System.Console.WriteLine();
                System.Console.WriteLine($"❌ Error running comparison: {ex.Message}");
                System.Console.WriteLine();
                System.Console.WriteLine($"Stack trace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    System.Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
                    System.Console.WriteLine($"Inner stack trace: {ex.InnerException.StackTrace}");
                }
            }

            if (!autoRun)
            {
                System.Console.WriteLine();
                System.Console.WriteLine("Press any key to exit...");
                try { System.Console.ReadKey(); } catch { /* Ignore */ }
            }
        }
    }

    private static int GetCustomShopperCount()
    {
        var input = System.Console.ReadLine()?.Trim();

        if (int.TryParse(input, out var count) && count > 0)
        {
            return count;
        }

        if (int.TryParse(input, out count) && count == 0)
        {
            return 0;
        }

        System.Console.WriteLine("Invalid input. Using default: 10,000");
        return 10_000;
    }

    private static RedisComparisonSettings ResolveRedisSettings(IConfiguration configuration)
    {
        var connectionString = configuration["RedisConnection:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            if (RedisConnectionStringParser.TryParse(connectionString, out var parsed, out _))
            {
                var password = parsed.Password ?? string.Empty;
                return new RedisComparisonSettings(
                    parsed.Host,
                    parsed.Port,
                    parsed.Username,
                    password,
                    "connection-string");
            }
        }

        var host = configuration["RedisConnection:Host"] ?? "127.0.0.1";
        var port = 6379;
        if (int.TryParse(configuration["RedisConnection:Port"], out var configuredPort) && configuredPort > 0)
            port = configuredPort;
        var username = configuration["RedisConnection:Username"];
        var configuredPassword = configuration["RedisConnection:Password"] ?? string.Empty;
        return new RedisComparisonSettings(host, port, username, configuredPassword, "host-port");
    }
}
