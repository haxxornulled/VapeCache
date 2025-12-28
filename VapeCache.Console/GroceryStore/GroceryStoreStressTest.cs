using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Threading.Channels;
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
public class GroceryStoreStressTest : BackgroundService
{
    private readonly GroceryStoreService _store;
    private readonly ICacheStats _stats;
    private readonly IRedisModuleDetector _moduleDetector;
    private readonly ILogger<GroceryStoreStressTest> _logger;

    private const int ConcurrentShoppers = 2000;  // Simulated concurrent users
    private const int TotalShoppers = 100000;     // Total users over test duration
    private const int TestDurationSeconds = 180;   // 3 minute stress test

    private readonly Random _random = new();
    private readonly Product[] _products = GroceryStoreService.GetAllProducts();

    public GroceryStoreStressTest(
        GroceryStoreService store,
        ICacheStats stats,
        IRedisModuleDetector moduleDetector,
        ILogger<GroceryStoreStressTest> logger)
    {
        _store = store;
        _stats = stats;
        _moduleDetector = moduleDetector;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for startup to complete
        await Task.Delay(5000, stoppingToken);

        _logger.LogInformation("==================================================");
        _logger.LogInformation("  GROCERY STORE STRESS TEST - BLACK FRIDAY MODE");
        _logger.LogInformation("==================================================");
        _logger.LogInformation("Concurrent Shoppers: {Count}", ConcurrentShoppers);
        _logger.LogInformation("Total Shoppers: {Count}", TotalShoppers);
        _logger.LogInformation("Test Duration: {Duration} seconds", TestDurationSeconds);

        // Detect Redis modules
        var modules = await _moduleDetector.GetInstalledModulesAsync(stoppingToken);
        if (modules.Length > 0)
            _logger.LogInformation("Redis Modules Detected: {Modules}", string.Join(", ", modules));
        else
            _logger.LogInformation("No Redis modules detected (vanilla Redis or in-memory mode)");

        // Create flash sales
        var flashSales = await CreateFlashSalesAsync(stoppingToken);
        _logger.LogInformation("Created {Count} flash sales", flashSales.Length);

        _logger.LogInformation("Starting stress test in 3 seconds...");
        await Task.Delay(3000, stoppingToken);

        var sw = Stopwatch.StartNew();
        var completedShoppers = 0;
        var channel = Channel.CreateBounded<int>(new BoundedChannelOptions(ConcurrentShoppers)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        // Stats reporting task
        var statsTask = ReportStatsAsync(sw, stoppingToken);

        // Producer: Generate shopper IDs (removed pacing delay for max throughput test)
        var producerTask = Task.Run(async () =>
        {
            for (var i = 1; i <= TotalShoppers && !stoppingToken.IsCancellationRequested; i++)
            {
                await channel.Writer.WriteAsync(i, stoppingToken);
            }
            channel.Writer.Complete();
        }, stoppingToken);

        // Consumers: Simulate shoppers
        var consumerTasks = Enumerable.Range(0, ConcurrentShoppers)
            .Select(async workerId =>
            {
                await foreach (var shopperId in channel.Reader.ReadAllAsync(stoppingToken))
                {
                    await SimulateShopperAsync(shopperId, flashSales, stoppingToken);
                    Interlocked.Increment(ref completedShoppers);
                }
            })
            .ToArray();

        // Wait for completion
        await producerTask;
        await Task.WhenAll(consumerTasks);
        sw.Stop();

        _logger.LogInformation("");
        _logger.LogInformation("==================================================");
        _logger.LogInformation("  STRESS TEST COMPLETE");
        _logger.LogInformation("==================================================");
        _logger.LogInformation("Total Shoppers Simulated: {Count}", completedShoppers);
        _logger.LogInformation("Total Duration: {Duration:F2} seconds", sw.Elapsed.TotalSeconds);
        _logger.LogInformation("Throughput: {Ops:F0} shoppers/sec", completedShoppers / sw.Elapsed.TotalSeconds);

        // Final stats
        await ReportFinalStatsAsync();
    }

    private async Task SimulateShopperAsync(int shopperId, FlashSale[] flashSales, CancellationToken ct)
    {
        var userId = $"user-{shopperId:D6}";
        var sessionId = $"session-{shopperId:D6}";

        try
        {
            // 1. Create user session (HSET)
            var session = new UserSession(
                userId,
                sessionId,
                DateTime.UtcNow,
                DateTime.UtcNow,
                Array.Empty<string>(),
                null);
            await _store.SaveSessionAsync(sessionId, session);

            // 2. Browse products (70% of shoppers)
            if (_random.Next(100) < 70)
            {
                var browsedProducts = Enumerable.Range(0, _random.Next(10, 25)) // Increased from 3-8 to 10-25 products browsed
                    .Select(_ => _products[_random.Next(_products.Length)])
                    .ToArray();

                foreach (var product in browsedProducts)
                {
                    await _store.GetProductAsync(product.Id);
                }
            }

            // 3. Join flash sale (30% of shoppers)
            if (_random.Next(100) < 30 && flashSales.Length > 0)
            {
                var sale = flashSales[_random.Next(flashSales.Length)];
                await _store.JoinFlashSaleAsync(sale.Id, userId);

                // Check if already in sale (test SISMEMBER)
                await _store.IsInFlashSaleAsync(sale.Id, userId);
            }

            // 4. Add items to cart (50% of shoppers)
            if (_random.Next(100) < 50)
            {
                var cartItems = _random.Next(15, 35); // Increased from 1-6 to 15-35 items per cart
                for (var i = 0; i < cartItems; i++)
                {
                    var product = _products[_random.Next(_products.Length)];
                    var item = new CartItem(
                        product.Id,
                        product.Name,
                        product.Price,
                        _random.Next(1, 10), // Increased quantity from 1-4 to 1-10 per item
                        DateTime.UtcNow);

                    await _store.AddToCartAsync(userId, item);
                }

                // Get cart count
                await _store.GetCartCountAsync(userId);
            }

            // 5. View cart (30% of shoppers)
            if (_random.Next(100) < 30)
            {
                await _store.GetCartAsync(userId);
            }

            // 6. Checkout/Clear cart (20% of shoppers)
            if (_random.Next(100) < 20)
            {
                await _store.ClearCartAsync(userId);
            }

            // 7. Remove item from cart (10% of shoppers)
            if (_random.Next(100) < 10)
            {
                await _store.RemoveFromCartAsync(userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error simulating shopper {UserId}", userId);
        }
    }

    private async Task<FlashSale[]> CreateFlashSalesAsync(CancellationToken ct)
    {
        var sales = new List<FlashSale>();

        // Create 5 flash sales with different products
        var saleProducts = _products.OrderBy(_ => _random.Next()).Take(5).ToArray();
        foreach (var product in saleProducts)
        {
            var sale = await _store.CreateFlashSaleAsync(
                product.Id,
                product.Price * 0.5m,  // 50% off
                _random.Next(50, 200),   // Limited quantity
                TimeSpan.FromMinutes(10));

            sales.Add(sale);
        }

        return sales.ToArray();
    }

    private async Task ReportStatsAsync(Stopwatch sw, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && sw.Elapsed < TimeSpan.FromSeconds(TestDurationSeconds))
        {
            await Task.Delay(10000, ct);  // Report every 10 seconds

            var elapsed = sw.Elapsed.TotalSeconds;
            var snapshot = _stats.Snapshot;
            var hitRate = snapshot.GetCalls > 0 ? (snapshot.Hits * 100.0 / snapshot.GetCalls) : 0;

            _logger.LogInformation(
                "[{Elapsed:F0}s] Cache Stats - Gets: {Gets} | Sets: {Sets} | Hits: {Hits} ({HitRate:F1}%) | Misses: {Misses}",
                elapsed, snapshot.GetCalls, snapshot.SetCalls, snapshot.Hits, hitRate, snapshot.Misses);
        }
    }

    private async Task ReportFinalStatsAsync()
    {
        // Sample some data to show what's cached
        _logger.LogInformation("");
        _logger.LogInformation("Sample Cached Data:");

        // Check a few flash sale participant counts
        var sale = await _store.GetFlashSaleAsync("sale-001");
        if (sale != null)
        {
            var count = await _store.GetFlashSaleParticipantCountAsync(sale.Id);
            _logger.LogInformation("  Flash Sale '{Name}': {Count} participants", sale.ProductName, count);
        }

        // Check a random cart
        var cartCount = await _store.GetCartCountAsync("user-000042");
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
}
