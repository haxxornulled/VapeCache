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
        var latencyMs = new double[shopperCount];
        var cartSizes = new int[shopperCount];
        var successCount = 0;
        var errorCount = 0;
        Exception? firstError = null;

        var shopperStart = Stopwatch.StartNew();
        var maxDegree = Math.Max(32, Environment.ProcessorCount * 8);
        var batchWriter = _service as ICartBatchWriter;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, shopperCount),
            new ParallelOptions { MaxDegreeOfParallelism = maxDegree },
            async (shopperIndex, ct) =>
            {
                var random = Random.Shared;

                try
                {
                    var shopperSw = Stopwatch.StartNew();
                    var userId = $"user-{shopperIndex:D6}";
                    var saleId = $"sale-{random.Next(1, 6)}";

                    // 1. Join flash sale
                    await _service.JoinFlashSaleAsync(saleId, userId);

                    // 2. Check if in sale
                    await _service.IsInFlashSaleAsync(saleId, userId);

                    // 3. Add random items to cart (15-35 items)
                    var cartSize = random.Next(15, maxCartSize + 1);
                    var items = new CartItem[cartSize];
                    for (int i = 0; i < cartSize; i++)
                    {
                        var product = products[random.Next(products.Length)];
                        items[i] = new CartItem(
                            product.Id,
                            product.Name,
                            product.Price,
                            random.Next(1, 4),
                            DateTime.UtcNow);
                    }

                    if (batchWriter is not null)
                    {
                        await batchWriter.AddToCartBatchAsync(userId, items).ConfigureAwait(false);
                    }
                    else
                    {
                        for (var i = 0; i < items.Length; i++)
                        {
                            await _service.AddToCartAsync(userId, items[i]).ConfigureAwait(false);
                        }
                    }

                    // 4. Get cart count
                    await _service.GetCartCountAsync(userId);

                    // 5. View cart
                    await _service.GetCartAsync(userId);

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
                    await _service.GetSessionAsync($"session:{userId}");

                    // 8. Get participant count
                    await _service.GetFlashSaleParticipantCountAsync(saleId);

                    // 9. Clear cart (checkout)
                    await _service.ClearCartAsync(userId);

                    shopperSw.Stop();
                    var slot = Interlocked.Increment(ref successCount) - 1;
                    latencyMs[slot] = shopperSw.Elapsed.TotalMilliseconds;
                    cartSizes[slot] = cartSize;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref errorCount);
                    Interlocked.CompareExchange(ref firstError, ex, comparand: null);
                    _logger.LogError(ex, "Shopper {Index} failed", shopperIndex);
                }
            });

        shopperStart.Stop();
        sw.Stop();

        // Calculate statistics
        var avgCartSize = successCount == 0 ? 0 : Average(cartSizes, successCount);
        var avgLatency = successCount == 0 ? 0 : Average(latencyMs, successCount);

        if (successCount > 0)
        {
            Array.Sort(latencyMs, 0, successCount);
        }

        var p50Latency = successCount == 0 ? 0 : Percentile(latencyMs, successCount, 0.50);
        var p95Latency = successCount == 0 ? 0 : Percentile(latencyMs, successCount, 0.95);
        var p99Latency = successCount == 0 ? 0 : Percentile(latencyMs, successCount, 0.99);
        var throughput = shopperStart.Elapsed.TotalSeconds > 0 ? successCount / shopperStart.Elapsed.TotalSeconds : 0;

        var result = new StressTestResult(
            ProviderName: _providerName,
            ShopperCount: shopperCount,
            SuccessCount: successCount,
            ErrorCount: errorCount,
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

        if (firstError != null)
        {
            _logger.LogWarning("First error: {Error}", firstError.Message);
        }

        return result;
    }

    private static double Percentile(double[] sortedValues, int count, double percentile)
    {
        if (count <= 0)
            return 0;

        var index = (int)Math.Ceiling((count - 1) * percentile);
        if (index < 0) index = 0;
        if (index >= count) index = count - 1;
        return sortedValues[index];
    }

    private static double Average(int[] values, int count)
    {
        if (count <= 0)
            return 0;

        long sum = 0;
        for (var i = 0; i < count; i++)
        {
            sum += values[i];
        }

        return sum / (double)count;
    }

    private static double Average(double[] values, int count)
    {
        if (count <= 0)
            return 0;

        double sum = 0;
        for (var i = 0; i < count; i++)
        {
            sum += values[i];
        }

        return sum / count;
    }
}

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
