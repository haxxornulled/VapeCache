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

        // Normalize GC baseline so per-provider deltas are comparable.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBytesStart = GC.GetTotalAllocatedBytes(precise: false);
        var gen0Start = GC.CollectionCount(0);
        var gen1Start = GC.CollectionCount(1);
        var gen2Start = GC.CollectionCount(2);

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
        var joinFlashSaleMs = new double[shopperCount];
        var isInFlashSaleMs = new double[shopperCount];
        var buildCartItemsMs = new double[shopperCount];
        var addToCartMs = new double[shopperCount];
        var cartReadPhaseMs = new double[shopperCount];
        var sessionAndSalePhaseMs = new double[shopperCount];
        var clearCartMs = new double[shopperCount];
        var successCount = 0;
        var errorCount = 0;
        Exception? firstError = null;

        var shopperStart = Stopwatch.StartNew();
        var maxDegree = GetMaxDegreeOfParallelism();
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
                    var now = DateTime.UtcNow;
                    double joinFlashSaleStep = 0;
                    double isInFlashSaleStep = 0;
                    double buildCartItemsStep = 0;
                    double addToCartStep = 0;
                    double cartReadPhaseStep = 0;
                    double sessionAndSalePhaseStep = 0;
                    double clearCartStep = 0;

                    // 1. Join flash sale
                    var stepStart = Stopwatch.GetTimestamp();
                    await _service.JoinFlashSaleAsync(saleId, userId);
                    joinFlashSaleStep = ElapsedMs(stepStart);

                    // 2. Check if in sale
                    stepStart = Stopwatch.GetTimestamp();
                    await _service.IsInFlashSaleAsync(saleId, userId);
                    isInFlashSaleStep = ElapsedMs(stepStart);

                    // 3. Add random items to cart (15-35 items)
                    stepStart = Stopwatch.GetTimestamp();
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
                            now);
                    }
                    buildCartItemsStep = ElapsedMs(stepStart);

                    stepStart = Stopwatch.GetTimestamp();
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
                    addToCartStep = ElapsedMs(stepStart);

                    // 4/5. Independent reads - issue together to reduce artificial serial latency.
                    stepStart = Stopwatch.GetTimestamp();
                    var cartCountTask = _service.GetCartCountAsync(userId);
                    var cartTask = _service.GetCartAsync(userId);
                    if (cartCountTask.IsCompletedSuccessfully)
                    {
                        _ = cartCountTask.Result;
                    }
                    else
                    {
                        await cartCountTask.ConfigureAwait(false);
                    }

                    if (cartTask.IsCompletedSuccessfully)
                    {
                        _ = cartTask.Result;
                    }
                    else
                    {
                        await cartTask.ConfigureAwait(false);
                    }
                    cartReadPhaseStep = ElapsedMs(stepStart);

                    // 6. Save session
                    stepStart = Stopwatch.GetTimestamp();
                    var session = new UserSession(
                        userId,
                        $"session-{shopperIndex}",
                        now,
                        now,
                        Array.Empty<string>(),
                        null);
                    var saveSessionTask = _service.SaveSessionAsync(userId, session);
                    if (saveSessionTask.IsCompletedSuccessfully)
                    {
                        saveSessionTask.GetAwaiter().GetResult();
                    }
                    else
                    {
                        await saveSessionTask.ConfigureAwait(false);
                    }

                    // 7/8. After save is durable, issue independent reads together.
                    var getSessionTask = _service.GetSessionAsync(userId);
                    var participantCountTask = _service.GetFlashSaleParticipantCountAsync(saleId);

                    if (getSessionTask.IsCompletedSuccessfully)
                    {
                        _ = getSessionTask.Result;
                    }
                    else
                    {
                        await getSessionTask.ConfigureAwait(false);
                    }

                    if (participantCountTask.IsCompletedSuccessfully)
                    {
                        _ = participantCountTask.Result;
                    }
                    else
                    {
                        await participantCountTask.ConfigureAwait(false);
                    }
                    sessionAndSalePhaseStep = ElapsedMs(stepStart);

                    // 9. Clear cart (checkout)
                    stepStart = Stopwatch.GetTimestamp();
                    await _service.ClearCartAsync(userId);
                    clearCartStep = ElapsedMs(stepStart);

                    shopperSw.Stop();
                    var slot = Interlocked.Increment(ref successCount) - 1;
                    latencyMs[slot] = shopperSw.Elapsed.TotalMilliseconds;
                    cartSizes[slot] = cartSize;
                    joinFlashSaleMs[slot] = joinFlashSaleStep;
                    isInFlashSaleMs[slot] = isInFlashSaleStep;
                    buildCartItemsMs[slot] = buildCartItemsStep;
                    addToCartMs[slot] = addToCartStep;
                    cartReadPhaseMs[slot] = cartReadPhaseStep;
                    sessionAndSalePhaseMs[slot] = sessionAndSalePhaseStep;
                    clearCartMs[slot] = clearCartStep;
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
        var avgCartSize = successCount == 0 ? 0m : Average(cartSizes, successCount);
        var avgLatency = successCount == 0 ? 0m : Average(latencyMs, successCount);

        if (successCount > 0)
        {
            Array.Sort(latencyMs, 0, successCount);
        }

        var p50Latency = successCount == 0 ? 0m : Percentile(latencyMs, successCount, 0.50);
        var p95Latency = successCount == 0 ? 0m : Percentile(latencyMs, successCount, 0.95);
        var p99Latency = successCount == 0 ? 0m : Percentile(latencyMs, successCount, 0.99);
        var p999Latency = successCount == 0 ? 0m : Percentile(latencyMs, successCount, 0.999);
        var throughput = shopperStart.Elapsed.TotalSeconds > 0
            ? successCount / (decimal)shopperStart.Elapsed.TotalSeconds
            : 0m;
        var allocatedBytes = Math.Max(0L, GC.GetTotalAllocatedBytes(precise: false) - allocatedBytesStart);
        var gen0Collections = Math.Max(0, GC.CollectionCount(0) - gen0Start);
        var gen1Collections = Math.Max(0, GC.CollectionCount(1) - gen1Start);
        var gen2Collections = Math.Max(0, GC.CollectionCount(2) - gen2Start);
        var bytesPerShopper = successCount <= 0 ? 0m : allocatedBytes / (decimal)successCount;
        var stepBreakdown = BuildStepBreakdown(
            successCount,
            ("JoinFlashSale", joinFlashSaleMs),
            ("IsInFlashSale", isInFlashSaleMs),
            ("BuildCartItems", buildCartItemsMs),
            ("AddToCart", addToCartMs),
            ("CartReadPhase", cartReadPhaseMs),
            ("SessionAndSalePhase", sessionAndSalePhaseMs),
            ("ClearCart", clearCartMs));

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
            P999LatencyMs: p999Latency,
            ThroughputShoppersPerSec: throughput,
            AllocatedBytes: allocatedBytes,
            Gen0Collections: gen0Collections,
            Gen1Collections: gen1Collections,
            Gen2Collections: gen2Collections);

        // Log results
        _logger.LogInformation("");
        _logger.LogInformation("===== {Provider} Results =====", _providerName);
        _logger.LogInformation("Total Duration: {Duration:N2}s", result.TotalDuration.TotalSeconds);
        _logger.LogInformation("Shopper Duration: {Duration:N2}s", result.ShopperDuration.TotalSeconds);
        _logger.LogInformation("Pre-Cache Duration: {Duration:N2}ms", result.PreCacheDuration.TotalMilliseconds);
        _logger.LogInformation("Success: {Success:N0} / {Total:N0} ({Percent:N2}%)",
            result.SuccessCount, shopperCount, (result.SuccessCount / (decimal)shopperCount) * 100m);
        _logger.LogInformation("Errors: {Errors:N0}", result.ErrorCount);
        _logger.LogInformation("Average Cart Size: {Avg:N1} items", result.AverageCartSize);
        _logger.LogInformation("Throughput: {Throughput:N0} shoppers/sec", result.ThroughputShoppersPerSec);
        _logger.LogInformation("Latency - Avg: {Avg:N2}ms, p50: {P50:N2}ms, p95: {P95:N2}ms, p99: {P99:N2}ms, p999: {P999:N2}ms",
            result.AverageLatencyMs, result.P50LatencyMs, result.P95LatencyMs, result.P99LatencyMs, result.P999LatencyMs);
        _logger.LogInformation(
            "GC/Alloc - Alloc: {AllocMb:N2} MB ({AllocPerShopper:N0} B/shopper), Gen0: {Gen0}, Gen1: {Gen1}, Gen2: {Gen2}",
            result.AllocatedBytes / (1024m * 1024m),
            bytesPerShopper,
            result.Gen0Collections,
            result.Gen1Collections,
            result.Gen2Collections);
        if (stepBreakdown.Length > 0)
        {
            _logger.LogInformation("Step Latency Breakdown (ms, sorted by p99):");
            foreach (var step in stepBreakdown.OrderByDescending(static s => s.P99Ms))
            {
                _logger.LogInformation(
                    "  {Step}: avg={Avg:N2}, p95={P95:N2}, p99={P99:N2}, p999={P999:N2}",
                    step.StepName,
                    step.AverageMs,
                    step.P95Ms,
                    step.P99Ms,
                    step.P999Ms);
            }

            var offender = stepBreakdown.MaxBy(static s => s.P99Ms);
            _logger.LogInformation(
                "Top p99 offender: {Step} ({P99:N2}ms)",
                offender.StepName,
                offender.P99Ms);
        }
        _logger.LogInformation("");

        if (firstError != null)
        {
            _logger.LogWarning("First error: {Error}", firstError.Message);
        }

        return result;
    }

    private static decimal Percentile(double[] sortedValues, int count, double percentile)
    {
        if (count <= 0)
            return 0m;

        var index = (int)Math.Ceiling((count - 1) * percentile);
        if (index < 0) index = 0;
        if (index >= count) index = count - 1;
        return (decimal)sortedValues[index];
    }

    private static decimal Average(int[] values, int count)
    {
        if (count <= 0)
            return 0m;

        long sum = 0;
        for (var i = 0; i < count; i++)
        {
            sum += values[i];
        }

        return sum / (decimal)count;
    }

    private static decimal Average(double[] values, int count)
    {
        if (count <= 0)
            return 0m;

        decimal sum = 0m;
        for (var i = 0; i < count; i++)
        {
            sum += (decimal)values[i];
        }

        return sum / count;
    }

    private static int GetMaxDegreeOfParallelism()
    {
        var env = Environment.GetEnvironmentVariable("VAPECACHE_BENCH_MAX_DEGREE");
        if (int.TryParse(env, out var configured) && configured > 0)
            return configured;

        return Math.Max(32, Environment.ProcessorCount * 8);
    }

    private static double ElapsedMs(long startTicks)
        => (Stopwatch.GetTimestamp() - startTicks) * 1000d / Stopwatch.Frequency;

    private static StepLatencySummary[] BuildStepBreakdown(
        int count,
        params (string Name, double[] Values)[] steps)
    {
        if (count <= 0 || steps.Length == 0)
            return Array.Empty<StepLatencySummary>();

        var summaries = new StepLatencySummary[steps.Length];
        for (var i = 0; i < steps.Length; i++)
            summaries[i] = SummarizeStep(steps[i].Name, steps[i].Values, count);
        return summaries;
    }

    private static StepLatencySummary SummarizeStep(string name, double[] values, int count)
    {
        var sorted = GC.AllocateUninitializedArray<double>(count);
        Array.Copy(values, 0, sorted, 0, count);
        Array.Sort(sorted, 0, count);
        return new StepLatencySummary(
            name,
            Average(sorted, count),
            Percentile(sorted, count, 0.95),
            Percentile(sorted, count, 0.99),
            Percentile(sorted, count, 0.999));
    }
}

public readonly record struct StepLatencySummary(
    string StepName,
    decimal AverageMs,
    decimal P95Ms,
    decimal P99Ms,
    decimal P999Ms);

public record StressTestResult(
    string ProviderName,
    int ShopperCount,
    int SuccessCount,
    int ErrorCount,
    TimeSpan TotalDuration,
    TimeSpan ShopperDuration,
    TimeSpan PreCacheDuration,
    decimal AverageCartSize,
    decimal AverageLatencyMs,
    decimal P50LatencyMs,
    decimal P95LatencyMs,
    decimal P99LatencyMs,
    decimal P999LatencyMs,
    decimal ThroughputShoppersPerSec,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);
