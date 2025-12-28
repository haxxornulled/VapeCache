using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace VapeCache.Console.GroceryStore;

/// <summary>
/// Head-to-head comparison stress test between VapeCache and StackExchange.Redis.
/// Runs identical workload on both implementations to demonstrate VapeCache superiority.
/// </summary>
public class GroceryStoreComparisonStressTest
{
    private readonly IGroceryStoreService _service;
    private readonly ILogger<GroceryStoreComparisonStressTest> _logger;
    private readonly string _providerName;

    public GroceryStoreComparisonStressTest(
        IGroceryStoreService service,
        ILogger<GroceryStoreComparisonStressTest> logger,
        string providerName)
    {
        _service = service;
        _logger = logger;
        _providerName = providerName;
    }

    /// <summary>
    /// Run comprehensive stress test and return performance metrics.
    /// </summary>
    public async Task<StressTestResult> RunStressTestAsync(int shopperCount = 10_000, int maxCartSize = 35)
    {
        _logger.LogInformation("===== {Provider} Grocery Store Stress Test =====", _providerName);
        _logger.LogInformation("Shoppers: {Count:N0}, Max Cart Size: {MaxCart}", shopperCount, maxCartSize);

        var sw = Stopwatch.StartNew();
        var products = GroceryStoreService.GetAllProducts();

        // Pre-cache all products
        var cacheStart = Stopwatch.StartNew();
        foreach (var product in products)
        {
            await _service.CacheProductAsync(product, TimeSpan.FromMinutes(10));
        }
        var cacheTime = cacheStart.Elapsed;
        _logger.LogInformation("Pre-cached {Count} products in {Ms}ms", products.Length, cacheTime.TotalMilliseconds);

        // Concurrent shopper operations
        var random = new Random();
        var stats = new ConcurrentBag<ShopperStats>();
        var errors = new ConcurrentBag<Exception>();

        var shopperStart = Stopwatch.StartNew();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, shopperCount),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 },
            async (shopperIndex, ct) =>
            {
                try
                {
                    var shopperSw = Stopwatch.StartNew();
                    var userId = $"user-{shopperIndex:D6}";
                    var saleId = $"sale-{random.Next(1, 6)}";

                    // 1. Join flash sale
                    await _service.JoinFlashSaleAsync(saleId, userId);

                    // 2. Check if in sale
                    var inSale = await _service.IsInFlashSaleAsync(saleId, userId);

                    // 3. Add random items to cart (15-35 items)
                    var cartSize = random.Next(15, maxCartSize + 1);
                    for (int i = 0; i < cartSize; i++)
                    {
                        var product = products[random.Next(products.Length)];
                        var cartItem = new CartItem(
                            product.Id,
                            product.Name,
                            product.Price,
                            random.Next(1, 4),
                            DateTime.UtcNow);
                        await _service.AddToCartAsync(userId, cartItem);
                    }

                    // 4. Get cart count
                    var count = await _service.GetCartCountAsync(userId);

                    // 5. View cart
                    var cart = await _service.GetCartAsync(userId);

                    // 6. Save session
                    var session = new UserSession(
                        userId,
                        $"session-{shopperIndex}",
                        DateTime.UtcNow,
                        DateTime.UtcNow,
                        Array.Empty<string>(),
                        null);
                    await _service.SaveSessionAsync($"session:{userId}", session);

                    // 7. Get session
                    var retrievedSession = await _service.GetSessionAsync($"session:{userId}");

                    // 8. Get participant count
                    var participantCount = await _service.GetFlashSaleParticipantCountAsync(saleId);

                    // 9. Clear cart (checkout)
                    await _service.ClearCartAsync(userId);

                    shopperSw.Stop();
                    stats.Add(new ShopperStats(
                        userId,
                        cartSize,
                        cart.Length,
                        shopperSw.Elapsed));
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                    _logger.LogError(ex, "Shopper {Index} failed", shopperIndex);
                }
            });

        shopperStart.Stop();
        sw.Stop();

        // Calculate statistics
        var statsList = stats.ToList();
        var avgCartSize = statsList.Average(s => s.CartSize);
        var avgLatency = statsList.Average(s => s.Latency.TotalMilliseconds);
        var p50Latency = statsList.OrderBy(s => s.Latency).ElementAt(statsList.Count / 2).Latency.TotalMilliseconds;
        var p95Latency = statsList.OrderBy(s => s.Latency).ElementAt((int)(statsList.Count * 0.95)).Latency.TotalMilliseconds;
        var p99Latency = statsList.OrderBy(s => s.Latency).ElementAt((int)(statsList.Count * 0.99)).Latency.TotalMilliseconds;
        var throughput = shopperCount / shopperStart.Elapsed.TotalSeconds;

        var result = new StressTestResult(
            ProviderName: _providerName,
            ShopperCount: shopperCount,
            SuccessCount: statsList.Count,
            ErrorCount: errors.Count,
            TotalDuration: sw.Elapsed,
            ShopperDuration: shopperStart.Elapsed,
            PreCacheDuration: cacheTime,
            AverageCartSize: avgCartSize,
            AverageLatencyMs: avgLatency,
            P50LatencyMs: p50Latency,
            P95LatencyMs: p95Latency,
            P99LatencyMs: p99Latency,
            ThroughputShoppersPerSec: throughput);

        // Log results
        _logger.LogInformation("");
        _logger.LogInformation("===== {Provider} Results =====", _providerName);
        _logger.LogInformation("Total Duration: {Duration:N2}s", result.TotalDuration.TotalSeconds);
        _logger.LogInformation("Shopper Duration: {Duration:N2}s", result.ShopperDuration.TotalSeconds);
        _logger.LogInformation("Pre-Cache Duration: {Duration:N2}ms", result.PreCacheDuration.TotalMilliseconds);
        _logger.LogInformation("Success: {Success:N0} / {Total:N0} ({Percent:N2}%)",
            result.SuccessCount, shopperCount, (result.SuccessCount / (double)shopperCount) * 100);
        _logger.LogInformation("Errors: {Errors:N0}", result.ErrorCount);
        _logger.LogInformation("Average Cart Size: {Avg:N1} items", result.AverageCartSize);
        _logger.LogInformation("Throughput: {Throughput:N0} shoppers/sec", result.ThroughputShoppersPerSec);
        _logger.LogInformation("Latency - Avg: {Avg:N2}ms, p50: {P50:N2}ms, p95: {P95:N2}ms, p99: {P99:N2}ms",
            result.AverageLatencyMs, result.P50LatencyMs, result.P95LatencyMs, result.P99LatencyMs);
        _logger.LogInformation("");

        if (errors.Any())
        {
            _logger.LogWarning("First error: {Error}", errors.First().Message);
        }

        return result;
    }
}

public record ShopperStats(
    string UserId,
    int CartSize,
    int RetrievedCartSize,
    TimeSpan Latency);

public record StressTestResult(
    string ProviderName,
    int ShopperCount,
    int SuccessCount,
    int ErrorCount,
    TimeSpan TotalDuration,
    TimeSpan ShopperDuration,
    TimeSpan PreCacheDuration,
    double AverageCartSize,
    double AverageLatencyMs,
    double P50LatencyMs,
    double P95LatencyMs,
    double P99LatencyMs,
    double ThroughputShoppersPerSec);
