using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Modules;

namespace VapeCache.Console.GroceryStore;

/// <summary>
/// Simulates Black Friday-level traffic to grocery store with:
/// - 10,000 concurrent shoppers
/// - Shopping cart operations (LIST)
/// - Flash sale participation (SET)
/// - User sessions (HASH)
/// - Product inventory queries
/// - Real-time metrics
/// </summary>
public class GroceryStoreStressTest : BackgroundService, IHostedLifecycleService
{
    private readonly IHostApplicationLifetime _hostLifetime;
    private readonly GroceryStoreService _store;
    private readonly ICacheStats _stats;
    private readonly IRedisModuleDetector _moduleDetector;
    private readonly IOptionsMonitor<GroceryStoreStressOptions> _optionsMonitor;
    private readonly ILogger<GroceryStoreStressTest> _logger;

    private readonly Product[] _products = GroceryStoreService.GetAllProducts();

    public GroceryStoreStressTest(
        IHostApplicationLifetime hostLifetime,
        GroceryStoreService store,
        ICacheStats stats,
        IRedisModuleDetector moduleDetector,
        IOptionsMonitor<GroceryStoreStressOptions> optionsMonitor,
        ILogger<GroceryStoreStressTest> logger)
    {
        _hostLifetime = hostLifetime;
        _store = store;
        _stats = stats;
        _moduleDetector = moduleDetector;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configured = _optionsMonitor.CurrentValue;
        if (!configured.Enabled)
        {
            _logger.LogInformation("Grocery store stress test is disabled.");
            return;
        }

        var workload = Normalize(configured);
        var hotProduct = ResolveHotProduct(workload.HotProductId);
        if (hotProduct is not null && workload.HotProductBiasPercent > 0)
        {
            _logger.LogInformation(
                "Hot product stampede mode enabled: {ProductId} ({ProductName}), Bias={Bias}%, ForceHotFlashSale={ForceSale}",
                hotProduct.Id,
                hotProduct.Name,
                workload.HotProductBiasPercent,
                workload.ForceHotProductFlashSale);
        }
        else if (!string.IsNullOrWhiteSpace(workload.HotProductId))
        {
            _logger.LogWarning(
                "HotProductId '{HotProductId}' not found in catalog. Falling back to random product distribution.",
                workload.HotProductId);
        }

        var workerCount = Math.Min(workload.ConcurrentShoppers, workload.TotalShoppers);

        // Wait for startup to complete
        if (workload.StartupDelay > TimeSpan.Zero)
            await Task.Delay(workload.StartupDelay, stoppingToken).ConfigureAwait(false);

        _logger.LogInformation("==================================================");
        _logger.LogInformation("  GROCERY STORE STRESS TEST - BLACK FRIDAY MODE");
        _logger.LogInformation("==================================================");
        _logger.LogInformation("Concurrent Shoppers: {Count}", workerCount);
        _logger.LogInformation("Total Shoppers: {Count}", workload.TotalShoppers);
        _logger.LogInformation("Target Duration: {Duration} seconds", workload.TargetDuration.TotalSeconds);

        // Detect Redis modules
        var modules = await _moduleDetector.GetInstalledModulesAsync(stoppingToken).ConfigureAwait(false);
        if (modules.Length > 0)
            _logger.LogInformation("Redis Modules Detected: {Modules}", string.Join(", ", modules));
        else
            _logger.LogInformation("No Redis modules detected (vanilla Redis or in-memory mode)");

        // Create flash sales
        var flashSales = await CreateFlashSalesAsync(hotProduct, workload.ForceHotProductFlashSale, stoppingToken).ConfigureAwait(false);
        _logger.LogInformation("Created {Count} flash sales", flashSales.Length);
        var hotFlashSale = hotProduct is null ? null : FindFlashSaleForProduct(flashSales, hotProduct.Id);
        var cartBatchWriter = _store as ICartBatchWriter;

        _logger.LogInformation("Starting stress test in {Countdown} seconds...", workload.CountdownDelay.TotalSeconds);
        if (workload.CountdownDelay > TimeSpan.Zero)
            await Task.Delay(workload.CountdownDelay, stoppingToken).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        var completedShoppers = 0;
        var nextShopperId = 0;

        using var statsCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var statsTask = ReportStatsAsync(sw, workload.TargetDuration, workload.StatsInterval, statsCts.Token);

        var workerTasks = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            workerTasks[i] = Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var shopperId = Interlocked.Increment(ref nextShopperId);
                    if (shopperId > workload.TotalShoppers)
                    {
                        break;
                    }

                    await SimulateShopperAsync(shopperId, flashSales, hotProduct, hotFlashSale, cartBatchWriter, workload, stoppingToken).ConfigureAwait(false);
                    Interlocked.Increment(ref completedShoppers);
                }
            }, stoppingToken);
        }

        await Task.WhenAll(workerTasks).ConfigureAwait(false);
        sw.Stop();

        statsCts.Cancel();
        await AwaitStatsTaskAsync(statsTask).ConfigureAwait(false);

        _logger.LogInformation("");
        _logger.LogInformation("==================================================");
        _logger.LogInformation("  STRESS TEST COMPLETE");
        _logger.LogInformation("==================================================");
        _logger.LogInformation("Total Shoppers Simulated: {Count}", completedShoppers);
        _logger.LogInformation("Total Duration: {Duration:F2} seconds", sw.Elapsed.TotalSeconds);
        _logger.LogInformation("Throughput: {Ops:F0} shoppers/sec", sw.Elapsed.TotalSeconds > 0 ? completedShoppers / sw.Elapsed.TotalSeconds : 0);

        // Final stats
        await ReportFinalStatsAsync(flashSales).ConfigureAwait(false);

        if (workload.StopHostOnCompletion)
        {
            _logger.LogInformation("Stopping host after grocery stress completion (StopHostOnCompletion=true).");
            _hostLifetime.StopApplication();
        }
    }

    private async Task SimulateShopperAsync(
        int shopperId,
        FlashSale[] flashSales,
        Product? hotProduct,
        FlashSale? hotFlashSale,
        ICartBatchWriter? cartBatchWriter,
        StressWorkload workload,
        CancellationToken ct)
    {
        var userId = $"user-{shopperId:D6}";
        var sessionId = $"session-{shopperId:D6}";
        var random = Random.Shared;

        try
        {
            ct.ThrowIfCancellationRequested();

            // 1. Create user session (HSET)
            var now = DateTime.UtcNow;
            var session = new UserSession(
                userId,
                sessionId,
                now,
                now,
                Array.Empty<string>(),
                null);
            await _store.SaveSessionAsync(sessionId, session).ConfigureAwait(false);

            // 2. Browse products
            if (random.Next(100) < workload.BrowseChancePercent)
            {
                var browseCount = random.Next(workload.BrowseMinProducts, workload.BrowseMaxProducts + 1);
                for (var i = 0; i < browseCount; i++)
                {
                    var product = SelectProduct(random, workload, hotProduct);
                    await _store.GetProductAsync(product.Id).ConfigureAwait(false);
                }
            }

            // 3. Join flash sale
            if (flashSales.Length > 0 && random.Next(100) < workload.FlashSaleJoinChancePercent)
            {
                var sale = workload.ForceHotProductFlashSale && hotFlashSale is not null
                    ? hotFlashSale
                    : flashSales[random.Next(flashSales.Length)];
                await _store.JoinFlashSaleAsync(sale.Id, userId).ConfigureAwait(false);

                // Check if already in sale (test SISMEMBER)
                await _store.IsInFlashSaleAsync(sale.Id, userId).ConfigureAwait(false);
            }

            // 4. Add items to cart
            if (random.Next(100) < workload.AddToCartChancePercent)
            {
                var cartItems = random.Next(workload.CartItemsMin, workload.CartItemsMax + 1);
                var addedAt = DateTime.UtcNow;
                var items = new CartItem[cartItems];
                for (var i = 0; i < items.Length; i++)
                {
                    var product = SelectProduct(random, workload, hotProduct);
                    items[i] = new CartItem(
                        product.Id,
                        product.Name,
                        product.Price,
                        random.Next(workload.CartItemQuantityMin, workload.CartItemQuantityMax + 1),
                        addedAt);
                }

                if (cartBatchWriter is not null)
                    await cartBatchWriter.AddToCartBatchAsync(userId, items).ConfigureAwait(false);
                else
                {
                    for (var i = 0; i < items.Length; i++)
                    {
                        await _store.AddToCartAsync(userId, items[i]).ConfigureAwait(false);
                    }
                }

                // Get cart count
                await _store.GetCartCountAsync(userId).ConfigureAwait(false);
            }

            // 5. View cart
            if (random.Next(100) < workload.ViewCartChancePercent)
            {
                await _store.GetCartAsync(userId).ConfigureAwait(false);
            }

            // 6. Checkout/Clear cart
            if (random.Next(100) < workload.CheckoutChancePercent)
            {
                await _store.ClearCartAsync(userId).ConfigureAwait(false);
            }

            // 7. Remove item from cart
            if (random.Next(100) < workload.RemoveFromCartChancePercent)
            {
                await _store.RemoveFromCartAsync(userId).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error simulating shopper {UserId}", userId);
        }
    }

    private async Task<FlashSale[]> CreateFlashSalesAsync(Product? hotProduct, bool forceHotProductFlashSale, CancellationToken ct)
    {
        var saleCount = Math.Min(5, _products.Length);
        if (saleCount == 0)
        {
            return Array.Empty<FlashSale>();
        }

        var random = Random.Shared;
        var selectedProductIndexes = SelectDistinctProductIndexes(_products.Length, saleCount);
        var sales = new List<FlashSale>(saleCount);

        if (forceHotProductFlashSale && hotProduct is not null)
        {
            sales.Add(await _store.CreateFlashSaleAsync(
                hotProduct.Id,
                hotProduct.Price * 0.5m,
                random.Next(50, 201),
                TimeSpan.FromMinutes(10)).ConfigureAwait(false));
        }

        for (var i = 0; i < saleCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (sales.Count >= saleCount)
                break;

            var product = _products[selectedProductIndexes[i]];
            if (forceHotProductFlashSale &&
                hotProduct is not null &&
                string.Equals(product.Id, hotProduct.Id, StringComparison.Ordinal))
            {
                continue;
            }

            sales.Add(await _store.CreateFlashSaleAsync(
                product.Id,
                product.Price * 0.5m,  // 50% off
                random.Next(50, 201),   // Limited quantity
                TimeSpan.FromMinutes(10)).ConfigureAwait(false));
        }

        while (sales.Count < saleCount)
        {
            ct.ThrowIfCancellationRequested();
            var product = _products[random.Next(_products.Length)];
            if (forceHotProductFlashSale &&
                hotProduct is not null &&
                string.Equals(product.Id, hotProduct.Id, StringComparison.Ordinal))
            {
                continue;
            }

            sales.Add(await _store.CreateFlashSaleAsync(
                product.Id,
                product.Price * 0.5m,
                random.Next(50, 201),
                TimeSpan.FromMinutes(10)).ConfigureAwait(false));
        }

        return sales.ToArray();
    }

    private async Task ReportStatsAsync(Stopwatch sw, TimeSpan targetDuration, TimeSpan statsInterval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && sw.Elapsed < targetDuration)
        {
            await Task.Delay(statsInterval, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                break;
            }

            var elapsed = sw.Elapsed.TotalSeconds;
            var snapshot = _stats.Snapshot;
            var hitRate = snapshot.GetCalls > 0 ? (snapshot.Hits * 100.0 / snapshot.GetCalls) : 0;

            _logger.LogInformation(
                "[{Elapsed:F0}s] Cache Stats - Gets: {Gets} | Sets: {Sets} | Hits: {Hits} ({HitRate:F1}%) | Misses: {Misses}",
                elapsed, snapshot.GetCalls, snapshot.SetCalls, snapshot.Hits, hitRate, snapshot.Misses);
        }
    }

    private async Task ReportFinalStatsAsync(FlashSale[] flashSales)
    {
        // Sample some data to show what's cached
        _logger.LogInformation("");
        _logger.LogInformation("Sample Cached Data:");

        // Check a flash sale participant count
        if (flashSales.Length > 0)
        {
            var sale = await _store.GetFlashSaleAsync(flashSales[0].Id).ConfigureAwait(false);
            if (sale != null)
            {
                var count = await _store.GetFlashSaleParticipantCountAsync(sale.Id).ConfigureAwait(false);
                _logger.LogInformation("  Flash Sale '{Name}': {Count} participants", sale.ProductName, count);
            }
        }

        // Check a random cart
        var cartCount = await _store.GetCartCountAsync("user-000042").ConfigureAwait(false);
        _logger.LogInformation("  Sample Cart (user-000042): {Count} items", cartCount);

        // Cache stats
        var finalSnapshot = _stats.Snapshot;
        _logger.LogInformation("");
        _logger.LogInformation("Final Cache Statistics:");
        _logger.LogInformation("  Total Gets: {Gets}", finalSnapshot.GetCalls);
        _logger.LogInformation("  Total Sets: {Sets}", finalSnapshot.SetCalls);
        _logger.LogInformation("  Total Hits: {Hits}", finalSnapshot.Hits);
        _logger.LogInformation("  Total Misses: {Misses}", finalSnapshot.Misses);
        _logger.LogInformation("  Hit Rate: {HitRate:F2}%", finalSnapshot.GetCalls > 0 ? (finalSnapshot.Hits * 100.0 / finalSnapshot.GetCalls) : 0);
        _logger.LogInformation("  Total Removes: {Removes}", finalSnapshot.RemoveCalls);
        _logger.LogInformation("  Fallback Events: {Fallback}", finalSnapshot.FallbackToMemory);
        _logger.LogInformation("  Circuit Breaker Opens: {Opens}", finalSnapshot.RedisBreakerOpened);
    }

    private static async Task AwaitStatsTaskAsync(Task statsTask)
    {
        try
        {
            await statsTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown/completion.
        }
    }

    private static int[] SelectDistinctProductIndexes(int productCount, int takeCount)
    {
        var indexes = new int[productCount];
        for (var i = 0; i < productCount; i++)
        {
            indexes[i] = i;
        }

        var random = Random.Shared;
        for (var i = 0; i < takeCount; i++)
        {
            var swapWith = random.Next(i, productCount);
            (indexes[i], indexes[swapWith]) = (indexes[swapWith], indexes[i]);
        }

        var result = new int[takeCount];
        Array.Copy(indexes, result, takeCount);
        return result;
    }

    private static FlashSale? FindFlashSaleForProduct(FlashSale[] sales, string productId)
    {
        for (var i = 0; i < sales.Length; i++)
        {
            if (string.Equals(sales[i].ProductId, productId, StringComparison.Ordinal))
                return sales[i];
        }

        return null;
    }

    private Product SelectProduct(Random random, StressWorkload workload, Product? hotProduct)
    {
        if (hotProduct is not null &&
            workload.HotProductBiasPercent > 0 &&
            random.Next(100) < workload.HotProductBiasPercent)
        {
            return hotProduct;
        }

        return _products[random.Next(_products.Length)];
    }

    private Product? ResolveHotProduct(string? hotProductId)
    {
        if (string.IsNullOrWhiteSpace(hotProductId))
            return null;

        for (var i = 0; i < _products.Length; i++)
        {
            var product = _products[i];
            if (string.Equals(product.Id, hotProductId, StringComparison.OrdinalIgnoreCase))
                return product;
        }

        return null;
    }

    private static StressWorkload Normalize(GroceryStoreStressOptions options)
    {
        var browseMinProducts = Math.Max(0, options.BrowseMinProducts);
        var browseMaxProducts = Math.Max(browseMinProducts, options.BrowseMaxProducts);

        var cartItemsMin = Math.Max(0, options.CartItemsMin);
        var cartItemsMax = Math.Max(cartItemsMin, options.CartItemsMax);

        var cartQuantityMin = Math.Max(1, options.CartItemQuantityMin);
        var cartQuantityMax = Math.Max(cartQuantityMin, options.CartItemQuantityMax);
        var hotProductId = string.IsNullOrWhiteSpace(options.HotProductId) ? null : options.HotProductId.Trim();

        return new StressWorkload(
            ConcurrentShoppers: Math.Max(1, options.ConcurrentShoppers),
            TotalShoppers: Math.Max(1, options.TotalShoppers),
            TargetDuration: TimeSpan.FromSeconds(Math.Max(1, options.TargetDurationSeconds)),
            StartupDelay: TimeSpan.FromSeconds(Math.Max(0, options.StartupDelaySeconds)),
            CountdownDelay: TimeSpan.FromSeconds(Math.Max(0, options.CountdownSeconds)),
            BrowseChancePercent: Math.Clamp(options.BrowseChancePercent, 0, 100),
            BrowseMinProducts: browseMinProducts,
            BrowseMaxProducts: browseMaxProducts,
            FlashSaleJoinChancePercent: Math.Clamp(options.FlashSaleJoinChancePercent, 0, 100),
            AddToCartChancePercent: Math.Clamp(options.AddToCartChancePercent, 0, 100),
            CartItemsMin: cartItemsMin,
            CartItemsMax: cartItemsMax,
            CartItemQuantityMin: cartQuantityMin,
            CartItemQuantityMax: cartQuantityMax,
            ViewCartChancePercent: Math.Clamp(options.ViewCartChancePercent, 0, 100),
            CheckoutChancePercent: Math.Clamp(options.CheckoutChancePercent, 0, 100),
            RemoveFromCartChancePercent: Math.Clamp(options.RemoveFromCartChancePercent, 0, 100),
            StatsInterval: TimeSpan.FromSeconds(Math.Max(1, options.StatsIntervalSeconds)),
            HotProductId: hotProductId,
            HotProductBiasPercent: Math.Clamp(options.HotProductBiasPercent, 0, 100),
            ForceHotProductFlashSale: options.ForceHotProductFlashSale,
            StopHostOnCompletion: options.StopHostOnCompletion);
    }

    private readonly record struct StressWorkload(
        int ConcurrentShoppers,
        int TotalShoppers,
        TimeSpan TargetDuration,
        TimeSpan StartupDelay,
        TimeSpan CountdownDelay,
        int BrowseChancePercent,
        int BrowseMinProducts,
        int BrowseMaxProducts,
        int FlashSaleJoinChancePercent,
        int AddToCartChancePercent,
        int CartItemsMin,
        int CartItemsMax,
        int CartItemQuantityMin,
        int CartItemQuantityMax,
        int ViewCartChancePercent,
        int CheckoutChancePercent,
        int RemoveFromCartChancePercent,
        TimeSpan StatsInterval,
        string? HotProductId,
        int HotProductBiasPercent,
        bool ForceHotProductFlashSale,
        bool StopHostOnCompletion);
}
