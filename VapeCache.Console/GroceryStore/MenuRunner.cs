using Microsoft.Extensions.Configuration;

namespace VapeCache.Console.GroceryStore;

/// <summary>
/// Interactive menu for running VapeCache vs StackExchange.Redis comparison.
/// </summary>
public static class MenuRunner
{
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

        if (!autoRun)
        {
            System.Console.WriteLine("Select test to run:");
            System.Console.WriteLine();
            System.Console.WriteLine("  [1] VapeCache vs StackExchange.Redis Comparison (10,000 shoppers)");
            System.Console.WriteLine("  [2] VapeCache vs StackExchange.Redis Comparison (50,000 shoppers)");
            System.Console.WriteLine("  [3] VapeCache vs StackExchange.Redis Comparison (100,000 shoppers)");
            System.Console.WriteLine("  [4] Custom shopper count");
            System.Console.WriteLine("  [0] Exit");
            System.Console.WriteLine();
            System.Console.Write("Enter selection: ");

            var choice = System.Console.ReadLine()?.Trim();

            shopperCount = choice switch
            {
                "1" => 10_000,
                "2" => 50_000,
                "3" => 100_000,
                "4" => GetCustomShopperCount(),
                "0" => 0,
                _ => 0
            };
        }

        if (shopperCount > 0)
        {
            System.Console.WriteLine();
            System.Console.WriteLine($"Starting comparison with {shopperCount:N0} shoppers...");
            System.Console.WriteLine("This may take a few minutes. Please wait...");
            System.Console.WriteLine();

            try
            {
                await ComparisonRunner.RunComparisonAsync(redisHost, redisPassword, shopperCount);
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
        System.Console.Write("Enter number of shoppers: ");
        var input = System.Console.ReadLine()?.Trim();

        if (int.TryParse(input, out var count) && count > 0)
        {
            return count;
        }

        System.Console.WriteLine("Invalid input. Using default: 10,000");
        return 10_000;
    }
}
