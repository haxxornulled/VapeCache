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
    private readonly int? _deterministicSeed;
    private readonly int? _maxDegreeOfParallelism;
    private readonly int _checkoutLaneCount;

    public GroceryStoreComparisonStressTest(
        IGroceryStoreService service,
        ILogger<GroceryStoreComparisonStressTest> logger,
        string providerName,
        int? deterministicSeed = null,
        int? maxDegreeOfParallelism = null,
        int checkoutLaneCount = 128)
    {
        _service = service;
        _logger = logger;
        _providerName = providerName;
        _deterministicSeed = deterministicSeed;
        _maxDegreeOfParallelism = maxDegreeOfParallelism;
        _checkoutLaneCount = Math.Max(1, checkoutLaneCount);
    }

    /// <summary>
    /// Run comprehensive stress test and return performance metrics.
    /// </summary>
    public async Task<StressTestResult> RunStressTestAsync(
        int shopperCount = 10_000,
        int maxCartSize = 35,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        maxCartSize = Math.Max(15, maxCartSize);
        var enableCommandCoverage = ResolvePhaseToggle("VAPECACHE_BENCH_ENABLE_COMMAND_COVERAGE", fallback: true);
        var enableTagInvalidation = ResolvePhaseToggle("VAPECACHE_BENCH_ENABLE_TAG_INVALIDATION", fallback: true);
        _logger.LogInformation("===== {Provider} Grocery Store Stress Test =====", _providerName);
        _logger.LogInformation("Shoppers: {Count:N0}, Max Cart Size: {MaxCart}", shopperCount, maxCartSize);
        _logger.LogInformation(
            "Workload Flags: CommandCoverage={CommandCoverage}, TagInvalidation={TagInvalidation}",
            enableCommandCoverage,
            enableTagInvalidation);

        var sw = Stopwatch.StartNew();
        var products = GroceryStoreService.GetAllProducts();

        // Pre-cache all products
        var cacheStart = Stopwatch.StartNew();
        foreach (var product in products)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _service.CacheProductAsync(product, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
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
        var commandCoveragePhaseMs = new double[shopperCount];
        var tagInvalidationPhaseMs = new double[shopperCount];
        var checkoutLaneWaitMs = new double[shopperCount];
        var clearCartMs = new double[shopperCount];
        var successCount = 0;
        var errorCount = 0;
        Exception? firstError = null;

        // Start allocation and GC baselines after harness arrays and pre-cache,
        // so bytes/op tracks shopper operations rather than harness setup.
        var allocatedBytesStart = GC.GetTotalAllocatedBytes(precise: false);
        var gen0Start = GC.CollectionCount(0);
        var gen1Start = GC.CollectionCount(1);
        var gen2Start = GC.CollectionCount(2);

        var shopperStart = Stopwatch.StartNew();
        var maxDegree = GetMaxDegreeOfParallelism(_maxDegreeOfParallelism);
        var checkoutLanes = CreateCheckoutLanes(_checkoutLaneCount);
        var batchWriter = _service as ICartBatchWriter;
        var tagOperations = _service as IGroceryStoreTagOperations;
        var commandCoverageRunner = _service as IGroceryStoreCommandCoverageRunner;

        async Task ProcessShopperAsync(int shopperIndex)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var deterministic = _deterministicSeed.HasValue;
                var seed = _deterministicSeed.GetValueOrDefault();
                var shopperSw = Stopwatch.StartNew();
                var userId = $"user-{shopperIndex:D6}";
                var saleId = deterministic
                    ? $"sale-{DeterministicRange(seed, shopperIndex, salt: 1, minInclusive: 1, maxInclusive: 5)}"
                    : $"sale-{Random.Shared.Next(1, 6)}";
                var now = deterministic
                    ? DeterministicTimestampUtc(shopperIndex)
                    : DateTime.UtcNow;
                double joinFlashSaleStep = 0;
                double isInFlashSaleStep = 0;
                double buildCartItemsStep = 0;
                double addToCartStep = 0;
                double cartReadPhaseStep = 0;
                double sessionAndSalePhaseStep = 0;
                double tagInvalidationPhaseStep = 0;
                double checkoutLaneWaitStep = 0;
                double clearCartStep = 0;

                // 1. Join flash sale
                var stepStart = Stopwatch.GetTimestamp();
                cancellationToken.ThrowIfCancellationRequested();
                await _service.JoinFlashSaleAsync(saleId, userId).ConfigureAwait(false);
                joinFlashSaleStep = ElapsedMs(stepStart);

                // 2. Check if in sale
                stepStart = Stopwatch.GetTimestamp();
                cancellationToken.ThrowIfCancellationRequested();
                await _service.IsInFlashSaleAsync(saleId, userId).ConfigureAwait(false);
                isInFlashSaleStep = ElapsedMs(stepStart);

                // 3. Add random items to cart (15-35 items)
                stepStart = Stopwatch.GetTimestamp();
                var cartSize = deterministic
                    ? DeterministicRange(seed, shopperIndex, salt: 2, minInclusive: 15, maxInclusive: maxCartSize)
                    : Random.Shared.Next(15, maxCartSize + 1);
                var items = new CartItem[cartSize];
                for (int i = 0; i < cartSize; i++)
                {
                    var productIndex = deterministic
                        ? DeterministicRange(seed, shopperIndex, salt: 1000 + (i * 2), minInclusive: 0, maxInclusive: products.Length - 1)
                        : Random.Shared.Next(products.Length);
                    var quantity = deterministic
                        ? DeterministicRange(seed, shopperIndex, salt: 1001 + (i * 2), minInclusive: 1, maxInclusive: 3)
                        : Random.Shared.Next(1, 4);
                    var product = products[productIndex];
                    items[i] = new CartItem(
                        product.Id,
                        product.Name,
                        product.Price,
                        quantity,
                        now);
                }
                buildCartItemsStep = ElapsedMs(stepStart);

                stepStart = Stopwatch.GetTimestamp();
                cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
                var session = new UserSession(
                    userId,
                    $"session-{shopperIndex}",
                    now,
                    now,
                    Array.Empty<string>(),
                    null);
                var saveSessionTask = _service.SaveSessionAsync(userId, session);
                var participantCountTask = _service.GetFlashSaleParticipantCountAsync(saleId);
                if (saveSessionTask.IsCompletedSuccessfully)
                {
                    saveSessionTask.GetAwaiter().GetResult();
                }
                else
                {
                    await saveSessionTask.ConfigureAwait(false);
                }

                // 7/8. Keep get-session after durable save, while participant count
                // runs in parallel because it is independent of session state.
                var getSessionTask = _service.GetSessionAsync(userId);

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

                // 9. Execute command-coverage pass across Redis types for this shopper.
                stepStart = Stopwatch.GetTimestamp();
                if (enableCommandCoverage && commandCoverageRunner is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await commandCoverageRunner.ExecuteShopperCommandCoverageAsync(
                        userId,
                        saleId,
                        userId,
                        now,
                        items,
                        cancellationToken).ConfigureAwait(false);
                }
                var commandCoveragePhaseStep = ElapsedMs(stepStart);

                // 10. Invalidate shopper tag scope and verify eviction visibility.
                stepStart = Stopwatch.GetTimestamp();
                if (enableTagInvalidation && tagOperations is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _ = await tagOperations.InvalidateShopperScopeAsync(userId, cancellationToken).ConfigureAwait(false);
                    _ = await _service.GetSessionAsync(userId).ConfigureAwait(false);
                    _ = await _service.GetCartCountAsync(userId).ConfigureAwait(false);
                }
                tagInvalidationPhaseStep = ElapsedMs(stepStart);

                // 11. Clear cart (checkout) through 128-lane cashier arbitration.
                stepStart = Stopwatch.GetTimestamp();
                cancellationToken.ThrowIfCancellationRequested();
                var laneId = deterministic
                    ? DeterministicRange(seed, shopperIndex, salt: 17_777, minInclusive: 0, maxInclusive: _checkoutLaneCount - 1)
                    : shopperIndex % _checkoutLaneCount;
                var lane = checkoutLanes[laneId];
                var laneWaitStart = Stopwatch.GetTimestamp();
                await lane.WaitAsync(cancellationToken).ConfigureAwait(false);
                checkoutLaneWaitStep = ElapsedMs(laneWaitStart);
                try
                {
                    await _service.ClearCartAsync(userId).ConfigureAwait(false);
                }
                finally
                {
                    lane.Release();
                }
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
                commandCoveragePhaseMs[slot] = commandCoveragePhaseStep;
                tagInvalidationPhaseMs[slot] = tagInvalidationPhaseStep;
                checkoutLaneWaitMs[slot] = checkoutLaneWaitStep;
                clearCartMs[slot] = clearCartStep;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Timeout-driven cancellation: stop scheduling additional shoppers without logging
                // disposal noise from downstream services.
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                // Transport/provider teardown can race with late shopper work during timeout cancellation.
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref errorCount);
                Interlocked.CompareExchange(ref firstError, ex, comparand: null);
                _logger.LogError(ex, "Shopper {Index} failed", shopperIndex);
            }
        }

        var nextShopper = -1;
        var workers = new Task[maxDegree];
        for (var worker = 0; worker < workers.Length; worker++)
        {
            workers[worker] = Task.Run(async () =>
            {
                while (true)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var shopperIndex = Interlocked.Increment(ref nextShopper);
                    if (shopperIndex >= shopperCount)
                        break;

                    await ProcessShopperAsync(shopperIndex).ConfigureAwait(false);
                }
            }, cancellationToken);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        for (var laneIndex = 0; laneIndex < checkoutLanes.Length; laneIndex++)
            checkoutLanes[laneIndex].Dispose();
        cancellationToken.ThrowIfCancellationRequested();

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
            ("CommandCoveragePhase", commandCoveragePhaseMs),
            ("TagInvalidationPhase", tagInvalidationPhaseMs),
            ("CheckoutLaneWait", checkoutLaneWaitMs),
            ("ClearCart", clearCartMs));
        var serviceTelemetry = _service is IGroceryStoreComparisonTelemetrySource telemetrySource
            ? telemetrySource.GetTelemetrySnapshot()
            : default;

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
            Gen2Collections: gen2Collections,
            ServiceReadOps: serviceTelemetry.ReadOps,
            ServiceWriteOps: serviceTelemetry.WriteOps,
            ServiceTotalOps: serviceTelemetry.TotalOps,
            ServiceCartItemWriteOps: serviceTelemetry.CartItemWriteOps,
            ServiceAdminOps: serviceTelemetry.AdminOps,
            ServiceOptionalSkips: serviceTelemetry.OptionalSkips);

        // Log results
        _logger.LogInformation("");
        _logger.LogInformation("===== {Provider} Results =====", _providerName);
        _logger.LogInformation("Checkout lanes: {LaneCount}", _checkoutLaneCount);
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
        if (result.SuccessCount > 0)
        {
            _logger.LogInformation(
                "Workload Integrity - ServiceOps: {TotalOps:N0} total ({ReadOps:N0} read / {WriteOps:N0} write), CartItemWrites: {CartItemWrites:N0}, Ops/Shopper: {OpsPerShopper:N2}",
                result.ServiceTotalOps,
                result.ServiceReadOps,
                result.ServiceWriteOps,
                result.ServiceCartItemWriteOps,
                result.ServiceTotalOps / (decimal)result.SuccessCount);
            if (result.ServiceAdminOps > 0 || result.ServiceOptionalSkips > 0)
            {
                _logger.LogInformation(
                    "Command Coverage - AdminOps: {AdminOps:N0}, OptionalSkips: {OptionalSkips:N0}, CoverageOps/Shopper: {OpsPerShopper:N2}",
                    result.ServiceAdminOps,
                    result.ServiceOptionalSkips,
                    (result.ServiceTotalOps - result.ServiceCartItemWriteOps) / (decimal)result.SuccessCount);
            }
        }
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

    private static SemaphoreSlim[] CreateCheckoutLanes(int laneCount)
    {
        var lanes = new SemaphoreSlim[laneCount];
        for (var i = 0; i < lanes.Length; i++)
            lanes[i] = new SemaphoreSlim(1, 1);

        return lanes;
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

    private static int GetMaxDegreeOfParallelism(int? configuredMaxDegree)
    {
        if (configuredMaxDegree is > 0)
            return configuredMaxDegree.Value;

        var env = Environment.GetEnvironmentVariable("VAPECACHE_BENCH_MAX_DEGREE");
        if (int.TryParse(env, out var configured) && configured > 0)
            return configured;

        // Default to a tuned window that avoids over-saturating scheduler/network queues on high-core hosts.
        return Math.Clamp(Environment.ProcessorCount * 3, 32, 96);
    }

    private static bool ResolvePhaseToggle(string variableName, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        return raw.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static double ElapsedMs(long startTicks)
        => (Stopwatch.GetTimestamp() - startTicks) * 1000d / Stopwatch.Frequency;

    private static DateTime DeterministicTimestampUtc(int shopperIndex)
    {
        var baseTicks = DateTime.UnixEpoch.Ticks;
        return new DateTime(baseTicks + (shopperIndex * TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    private static int DeterministicRange(int seed, int shopperIndex, int salt, int minInclusive, int maxInclusive)
    {
        if (maxInclusive <= minInclusive)
            return minInclusive;

        var span = maxInclusive - minInclusive + 1;
        var hash = DeterministicHash((uint)seed, (uint)shopperIndex, (uint)salt);
        return minInclusive + (int)(hash % (uint)span);
    }

    private static uint DeterministicHash(uint seed, uint shopperIndex, uint salt)
    {
        var x = seed;
        x ^= shopperIndex * 0x9E3779B9u;
        x ^= salt * 0x85EBCA6Bu;
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        x ^= x >> 16;
        return x;
    }

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

public readonly record struct StepLatencySummary
{
    public StepLatencySummary(
        string StepName,
        decimal AverageMs,
        decimal P95Ms,
        decimal P99Ms,
        decimal P999Ms)
    {
        this.StepName = StepName;
        this.AverageMs = AverageMs;
        this.P95Ms = P95Ms;
        this.P99Ms = P99Ms;
        this.P999Ms = P999Ms;
    }

    public string StepName { get; init; }
    public decimal AverageMs { get; init; }
    public decimal P95Ms { get; init; }
    public decimal P99Ms { get; init; }
    public decimal P999Ms { get; init; }
}

public record StressTestResult
{
    public StressTestResult(
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
        int Gen2Collections,
        long ServiceReadOps = 0,
        long ServiceWriteOps = 0,
        long ServiceTotalOps = 0,
        long ServiceCartItemWriteOps = 0,
        long ServiceAdminOps = 0,
        long ServiceOptionalSkips = 0)
    {
        this.ProviderName = ProviderName;
        this.ShopperCount = ShopperCount;
        this.SuccessCount = SuccessCount;
        this.ErrorCount = ErrorCount;
        this.TotalDuration = TotalDuration;
        this.ShopperDuration = ShopperDuration;
        this.PreCacheDuration = PreCacheDuration;
        this.AverageCartSize = AverageCartSize;
        this.AverageLatencyMs = AverageLatencyMs;
        this.P50LatencyMs = P50LatencyMs;
        this.P95LatencyMs = P95LatencyMs;
        this.P99LatencyMs = P99LatencyMs;
        this.P999LatencyMs = P999LatencyMs;
        this.ThroughputShoppersPerSec = ThroughputShoppersPerSec;
        this.AllocatedBytes = AllocatedBytes;
        this.Gen0Collections = Gen0Collections;
        this.Gen1Collections = Gen1Collections;
        this.Gen2Collections = Gen2Collections;
        this.ServiceReadOps = ServiceReadOps;
        this.ServiceWriteOps = ServiceWriteOps;
        this.ServiceTotalOps = ServiceTotalOps;
        this.ServiceCartItemWriteOps = ServiceCartItemWriteOps;
        this.ServiceAdminOps = ServiceAdminOps;
        this.ServiceOptionalSkips = ServiceOptionalSkips;
    }

    public string ProviderName { get; init; }
    public int ShopperCount { get; init; }
    public int SuccessCount { get; init; }
    public int ErrorCount { get; init; }
    public TimeSpan TotalDuration { get; init; }
    public TimeSpan ShopperDuration { get; init; }
    public TimeSpan PreCacheDuration { get; init; }
    public decimal AverageCartSize { get; init; }
    public decimal AverageLatencyMs { get; init; }
    public decimal P50LatencyMs { get; init; }
    public decimal P95LatencyMs { get; init; }
    public decimal P99LatencyMs { get; init; }
    public decimal P999LatencyMs { get; init; }
    public decimal ThroughputShoppersPerSec { get; init; }
    public long AllocatedBytes { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public long ServiceReadOps { get; init; }
    public long ServiceWriteOps { get; init; }
    public long ServiceTotalOps { get; init; }
    public long ServiceCartItemWriteOps { get; init; }
    public long ServiceAdminOps { get; init; }
    public long ServiceOptionalSkips { get; init; }
}
