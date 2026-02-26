using Microsoft.Extensions.Configuration;

namespace VapeCache.Console.GroceryStore;

/// <summary>
/// Interactive menu for running VapeCache vs StackExchange.Redis comparison.
/// </summary>
public static class MenuRunner
{
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

        // Get Redis connection details from configuration
        var redisHost = configuration["RedisConnection:Host"] ?? "192.168.100.50";
        var redisPassword = configuration["RedisConnection:Password"] ?? "redis4me!!";

        System.Console.WriteLine($"Redis Host: {redisHost}");
        // SECURITY FIX: Don't log authentication status to prevent credential enumeration attacks
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
                await ComparisonRunner.RunComparisonAsync(configuration, redisHost, redisPassword, shopperCount, maxCartSize);
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
}
