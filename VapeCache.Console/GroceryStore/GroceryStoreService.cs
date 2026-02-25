using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Collections;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Console.GroceryStore;

/// <summary>
/// High-performance grocery store cache service using VapeCache typed collections.
/// Demonstrates LIST, SET, and HASH operations under heavy load.
/// Implements IGroceryStoreService for head-to-head comparison with StackExchange.Redis.
/// </summary>
public class GroceryStoreService : IGroceryStoreService
{
    private readonly ICacheCollectionFactory _collections;
    private readonly IVapeCache _cache;
    private readonly IRedisCommandExecutor _executor;
    private readonly ILogger<GroceryStoreService> _logger;
    private readonly ICacheHash<UserSession> _sessions;
    private readonly ConcurrentDictionary<string, ICacheList<CartItem>> _cartLists = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ICacheSet<string>> _flashSaleParticipants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ICacheList<InventoryUpdate>> _inventoryUpdates = new(StringComparer.Ordinal);

    // Pre-defined product catalog
    private static readonly Product[] Products = GenerateProducts();
    private static readonly IReadOnlyDictionary<string, Product> ProductsById = BuildProductMap(Products);
    private static readonly IReadOnlyDictionary<string, CacheKey<Product>> ProductCacheKeys = BuildProductCacheKeyMap(Products);

    public GroceryStoreService(
        ICacheCollectionFactory collections,
        IVapeCache cache,
        IRedisCommandExecutor executor,
        ILogger<GroceryStoreService> logger)
    {
        _collections = collections;
        _cache = cache;
        _executor = executor;
        _logger = logger;
        _sessions = _collections.Hash<UserSession>("sessions:active");
    }

    // ========== Shopping Cart Operations (LIST) ==========

    /// <summary>
    /// Add item to user's shopping cart (LPUSH)
    /// </summary>
    public async ValueTask AddToCartAsync(string userId, CartItem item)
    {
        var cart = GetCartList(userId);
        await cart.PushFrontAsync(item);  // Most recent items first
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Added {Product} to cart for user {UserId}", item.ProductName, userId);
    }

    /// <summary>
    /// Remove last item from cart (RPOP)
    /// </summary>
    public async Task<CartItem?> RemoveFromCartAsync(string userId)
    {
        var cart = GetCartList(userId);
        var removed = await cart.PopBackAsync();
        if (removed != null && _logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Removed {Product} from cart for user {UserId}", removed.ProductName, userId);
        return removed;
    }

    /// <summary>
    /// Get current cart items (LRANGE)
    /// </summary>
    public async ValueTask<CartItem[]> GetCartAsync(string userId)
    {
        var cart = GetCartList(userId);
        return await cart.RangeAsync(0, -1);  // Get all items
    }

    /// <summary>
    /// Get cart item count (LLEN)
    /// </summary>
    public async ValueTask<long> GetCartCountAsync(string userId)
    {
        var cart = GetCartList(userId);
        return await cart.LengthAsync();
    }

    /// <summary>
    /// Clear cart by deleting the cart key in one operation.
    /// </summary>
    public async ValueTask ClearCartAsync(string userId)
    {
        var cart = GetCartList(userId);
        var cartKey = cart.Key;
        long count = 0;
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            count = await cart.LengthAsync();
        }

        await _executor.DeleteAsync(cartKey, CancellationToken.None);
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Cleared {Count} items from cart for user {UserId}", count, userId);
    }

    // ========== Flash Sale Participants (SET) ==========

    /// <summary>
    /// User joins flash sale (SADD - idempotent)
    /// </summary>
    public async ValueTask JoinFlashSaleAsync(string saleId, string userId)
    {
        var participants = GetFlashSaleParticipantsSet(saleId);
        var added = await participants.AddAsync(userId);
        if (added > 0 && _logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("User {UserId} joined flash sale {SaleId}", userId, saleId);
    }

    /// <summary>
    /// Check if user is in flash sale (SISMEMBER - O(1))
    /// </summary>
    public async ValueTask<bool> IsInFlashSaleAsync(string saleId, string userId)
    {
        var participants = GetFlashSaleParticipantsSet(saleId);
        return await participants.ContainsAsync(userId);
    }

    /// <summary>
    /// Get all flash sale participants (SMEMBERS)
    /// </summary>
    public async Task<string[]> GetFlashSaleParticipantsAsync(string saleId)
    {
        var participants = GetFlashSaleParticipantsSet(saleId);
        return await participants.MembersAsync();
    }

    /// <summary>
    /// Get participant count (SCARD)
    /// </summary>
    public async ValueTask<long> GetFlashSaleParticipantCountAsync(string saleId)
    {
        var participants = GetFlashSaleParticipantsSet(saleId);
        return await participants.CountAsync();
    }

    /// <summary>
    /// User leaves flash sale (SREM)
    /// </summary>
    public async Task<bool> LeaveFlashSaleAsync(string saleId, string userId)
    {
        var participants = GetFlashSaleParticipantsSet(saleId);
        var removed = await participants.RemoveAsync(userId);
        if (removed > 0 && _logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("User {UserId} left flash sale {SaleId}", userId, saleId);
        return removed > 0;
    }

    // ========== User Sessions (HASH) ==========

    /// <summary>
    /// Save user session (HSET)
    /// </summary>
    public async ValueTask SaveSessionAsync(string sessionId, UserSession session)
    {
        await _sessions.SetAsync(sessionId, session);
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Saved session {SessionId} for user {UserId}", sessionId, session.UserId);
    }

    /// <summary>
    /// Get user session (HGET)
    /// </summary>
    public async ValueTask<UserSession?> GetSessionAsync(string sessionId)
    {
        return await _sessions.GetAsync(sessionId);
    }

    /// <summary>
    /// Get multiple sessions (HMGET)
    /// </summary>
    public async Task<UserSession?[]> GetSessionsAsync(params string[] sessionIds)
    {
        return await _sessions.GetManyAsync(sessionIds);
    }

    // ========== Product Inventory (Simple Cache) ==========

    /// <summary>
    /// Get product by ID (with cache-aside pattern)
    /// </summary>
    public async ValueTask<Product?> GetProductAsync(string productId)
    {
        var key = ResolveProductCacheKey(productId);
        var product = await _cache.GetAsync(key);
        if (product != null) return product;

        // Cache miss - load from "database" (our static array)
        if (!ProductsById.TryGetValue(productId, out product))
            return null;

        if (product != null)
        {
            await _cache.SetAsync(key, product, new CacheEntryOptions { Ttl = TimeSpan.FromMinutes(10) });
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Cached product {ProductId} from database", productId);
        }

        return product;
    }

    /// <summary>
    /// Cache a product with TTL (IGroceryStoreService interface)
    /// </summary>
    public async ValueTask CacheProductAsync(Product product, TimeSpan ttl)
    {
        var key = ResolveProductCacheKey(product.Id);
        await _cache.SetAsync(key, product, new CacheEntryOptions { Ttl = ttl });
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Cached product {ProductId} with TTL {Ttl}", product.Id, ttl);
    }

    /// <summary>
    /// Update product inventory
    /// </summary>
    public async Task UpdateInventoryAsync(string productId, int newQuantity)
    {
        var product = await GetProductAsync(productId);
        if (product == null) return;

        var updated = product with { StockQuantity = newQuantity };
        var key = new CacheKey<Product>($"product:{productId}");
        await _cache.SetAsync(key, updated, new CacheEntryOptions { Ttl = TimeSpan.FromMinutes(10) });

        // Track inventory updates
        var updates = GetInventoryUpdatesList(productId);
        await updates.PushFrontAsync(new InventoryUpdate(
            productId,
            product.StockQuantity,
            newQuantity,
            "Admin update",
            DateTime.UtcNow));

        _logger.LogInformation("Updated inventory for {ProductId}: {Old} -> {New}",
            productId, product.StockQuantity, newQuantity);
    }

    /// <summary>
    /// Get inventory update history for a product (LRANGE)
    /// </summary>
    public async Task<InventoryUpdate[]> GetInventoryHistoryAsync(string productId, int limit = 10)
    {
        var updates = GetInventoryUpdatesList(productId);
        return await updates.RangeAsync(0, limit - 1);
    }

    // ========== Flash Sale Management ==========

    /// <summary>
    /// Create a flash sale
    /// </summary>
    public async Task<FlashSale> CreateFlashSaleAsync(string productId, decimal salePrice, int quantity, TimeSpan duration)
    {
        var product = await GetProductAsync(productId);
        if (product == null)
            throw new InvalidOperationException($"Product {productId} not found");

        var sale = new FlashSale(
            Guid.NewGuid().ToString("N"),
            productId,
            product.Name,
            product.Price,
            salePrice,
            quantity,
            quantity,
            DateTime.UtcNow,
            DateTime.UtcNow.Add(duration));

        var saleKey = new CacheKey<FlashSale>($"sale:{sale.Id}");
        await _cache.SetAsync(saleKey, sale, new CacheEntryOptions { Ttl = duration });
        _logger.LogInformation("Created flash sale {SaleId} for {Product}: ${OriginalPrice} -> ${SalePrice} ({Quantity} available)",
            sale.Id, sale.ProductName, sale.OriginalPrice, sale.SalePrice, quantity);

        return sale;
    }

    /// <summary>
    /// Get active flash sale
    /// </summary>
    public async Task<FlashSale?> GetFlashSaleAsync(string saleId)
    {
        var saleKey = new CacheKey<FlashSale>($"sale:{saleId}");
        return await _cache.GetAsync(saleKey);
    }

    // ========== Helpers ==========

    private static Product[] GenerateProducts()
    {
        return new[]
        {
            // Fresh Produce
            new Product("prod-001", "Organic Bananas", "Produce", 2.99m, 500, "/img/bananas.jpg"),
            new Product("prod-002", "Strawberries 1lb", "Produce", 4.99m, 300, "/img/strawberries.jpg"),
            new Product("prod-003", "Romaine Lettuce", "Produce", 3.49m, 200, "/img/lettuce.jpg"),
            new Product("prod-004", "Avocados (3-pack)", "Produce", 5.99m, 400, "/img/avocados.jpg"),
            new Product("prod-005", "Baby Carrots 1lb", "Produce", 1.99m, 600, "/img/carrots.jpg"),

            // Dairy
            new Product("prod-006", "Organic Whole Milk", "Dairy", 4.29m, 250, "/img/milk.jpg"),
            new Product("prod-007", "Greek Yogurt (6-pack)", "Dairy", 6.99m, 180, "/img/yogurt.jpg"),
            new Product("prod-008", "Cheddar Cheese 8oz", "Dairy", 4.49m, 220, "/img/cheese.jpg"),
            new Product("prod-009", "Butter 16oz", "Dairy", 5.99m, 300, "/img/butter.jpg"),
            new Product("prod-010", "Eggs (Dozen)", "Dairy", 3.99m, 500, "/img/eggs.jpg"),

            // Bakery
            new Product("prod-011", "Sourdough Bread", "Bakery", 4.99m, 150, "/img/bread.jpg"),
            new Product("prod-012", "Croissants (6-pack)", "Bakery", 5.49m, 100, "/img/croissants.jpg"),
            new Product("prod-013", "Bagels (6-pack)", "Bakery", 3.99m, 200, "/img/bagels.jpg"),

            // Meat & Seafood
            new Product("prod-014", "Chicken Breast 1lb", "Meat", 7.99m, 180, "/img/chicken.jpg"),
            new Product("prod-015", "Ground Beef 1lb", "Meat", 6.99m, 220, "/img/beef.jpg"),
            new Product("prod-016", "Salmon Fillet 8oz", "Seafood", 12.99m, 120, "/img/salmon.jpg"),

            // Pantry
            new Product("prod-017", "Pasta (16oz)", "Pantry", 1.99m, 800, "/img/pasta.jpg"),
            new Product("prod-018", "Olive Oil 500ml", "Pantry", 9.99m, 150, "/img/oil.jpg"),
            new Product("prod-019", "Canned Tomatoes", "Pantry", 1.49m, 600, "/img/tomatoes.jpg"),
            new Product("prod-020", "Rice 2lb", "Pantry", 3.99m, 400, "/img/rice.jpg"),

            // Beverages
            new Product("prod-021", "Orange Juice 64oz", "Beverages", 4.99m, 250, "/img/oj.jpg"),
            new Product("prod-022", "Sparkling Water (12-pack)", "Beverages", 5.99m, 300, "/img/water.jpg"),
            new Product("prod-023", "Coffee Beans 12oz", "Beverages", 11.99m, 180, "/img/coffee.jpg"),

            // Frozen
            new Product("prod-024", "Ice Cream Pint", "Frozen", 5.49m, 200, "/img/icecream.jpg"),
            new Product("prod-025", "Frozen Pizza", "Frozen", 6.99m, 250, "/img/pizza.jpg"),
        };
    }

    private static IReadOnlyDictionary<string, Product> BuildProductMap(Product[] products)
    {
        var map = new Dictionary<string, Product>(products.Length, StringComparer.Ordinal);
        foreach (var product in products)
        {
            map[product.Id] = product;
        }

        return map;
    }

    private static IReadOnlyDictionary<string, CacheKey<Product>> BuildProductCacheKeyMap(Product[] products)
    {
        var map = new Dictionary<string, CacheKey<Product>>(products.Length, StringComparer.Ordinal);
        foreach (var product in products)
        {
            map[product.Id] = new CacheKey<Product>($"product:{product.Id}");
        }

        return map;
    }

    private CacheKey<Product> ResolveProductCacheKey(string productId)
    {
        return ProductCacheKeys.TryGetValue(productId, out var key)
            ? key
            : new CacheKey<Product>($"product:{productId}");
    }

    private ICacheList<CartItem> GetCartList(string userId)
    {
        return _cartLists.GetOrAdd(userId, static (uid, factory) => factory.List<CartItem>($"cart:{uid}"), _collections);
    }

    private ICacheSet<string> GetFlashSaleParticipantsSet(string saleId)
    {
        return _flashSaleParticipants.GetOrAdd(saleId, static (id, factory) => factory.Set<string>($"sale:{id}:participants"), _collections);
    }

    private ICacheList<InventoryUpdate> GetInventoryUpdatesList(string productId)
    {
        return _inventoryUpdates.GetOrAdd(productId, static (id, factory) => factory.List<InventoryUpdate>($"inventory:updates:{id}"), _collections);
    }

    public static Product[] GetAllProducts() => Products;
}
