namespace VapeCache.Console.GroceryStore;

/// <summary>
/// Product in the grocery store inventory
/// </summary>
public record Product
{
    public Product(string id, string name, string category, decimal price, int stockQuantity, string imageUrl)
    {
        Id = id;
        Name = name;
        Category = category;
        Price = price;
        StockQuantity = stockQuantity;
        ImageUrl = imageUrl;
    }

    public string Id { get; init; }
    public string Name { get; init; }
    public string Category { get; init; }
    public decimal Price { get; init; }
    public int StockQuantity { get; init; }
    public string ImageUrl { get; init; }
    public string Department { get; init; } = string.Empty;
    public string Aisle { get; init; } = string.Empty;
    public string Brand { get; init; } = string.Empty;
    public string UnitOfMeasure { get; init; } = string.Empty;
    public string TemperatureZone { get; init; } = string.Empty;
    public bool IsWeightedItem { get; init; }
    public bool RequiresIdCheck { get; init; }
}

/// <summary>
/// Shopping cart item
/// </summary>
public record CartItem
{
    public CartItem(string productId, string productName, decimal price, int quantity, DateTime addedAt)
    {
        ProductId = productId;
        ProductName = productName;
        Price = price;
        Quantity = quantity;
        AddedAt = addedAt;
    }

    public string ProductId { get; init; }
    public string ProductName { get; init; }
    public decimal Price { get; init; }
    public int Quantity { get; init; }
    public DateTime AddedAt { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Department { get; init; } = string.Empty;
    public string Aisle { get; init; } = string.Empty;
    public string Brand { get; init; } = string.Empty;
    public string UnitOfMeasure { get; init; } = string.Empty;
    public string TemperatureZone { get; init; } = string.Empty;
    public decimal ExtendedPrice { get; init; }
}

/// <summary>
/// User session data (active cart, preferences, etc.)
/// </summary>
public record UserSession
{
    public UserSession(
        string userId,
        string sessionId,
        DateTime createdAt,
        DateTime lastActivityAt,
        string[] recentlyViewedProductIds,
        string? activeCartId)
    {
        UserId = userId;
        SessionId = sessionId;
        CreatedAt = createdAt;
        LastActivityAt = lastActivityAt;
        RecentlyViewedProductIds = recentlyViewedProductIds;
        ActiveCartId = activeCartId;
    }

    public string UserId { get; init; }
    public string SessionId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastActivityAt { get; init; }
    public string[] RecentlyViewedProductIds { get; init; }
    public string? ActiveCartId { get; init; }
    public string StoreId { get; init; } = string.Empty;
    public string LoyaltyTier { get; init; } = string.Empty;
    public string FulfillmentMethod { get; init; } = string.Empty;
    public string[] CouponCodes { get; init; } = Array.Empty<string>();
    public string[] DietaryPreferences { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Flash sale event with limited inventory
/// </summary>
public record FlashSale
{
    public FlashSale(
        string id,
        string productId,
        string productName,
        decimal originalPrice,
        decimal salePrice,
        int totalQuantity,
        int remainingQuantity,
        DateTime startsAt,
        DateTime endsAt)
    {
        Id = id;
        ProductId = productId;
        ProductName = productName;
        OriginalPrice = originalPrice;
        SalePrice = salePrice;
        TotalQuantity = totalQuantity;
        RemainingQuantity = remainingQuantity;
        StartsAt = startsAt;
        EndsAt = endsAt;
    }

    public string Id { get; init; }
    public string ProductId { get; init; }
    public string ProductName { get; init; }
    public decimal OriginalPrice { get; init; }
    public decimal SalePrice { get; init; }
    public int TotalQuantity { get; init; }
    public int RemainingQuantity { get; init; }
    public DateTime StartsAt { get; init; }
    public DateTime EndsAt { get; init; }
}

/// <summary>
/// Real-time inventory update event
/// </summary>
public record InventoryUpdate
{
    public InventoryUpdate(string productId, int oldQuantity, int newQuantity, string reason, DateTime timestamp)
    {
        ProductId = productId;
        OldQuantity = oldQuantity;
        NewQuantity = newQuantity;
        Reason = reason;
        Timestamp = timestamp;
    }

    public string ProductId { get; init; }
    public int OldQuantity { get; init; }
    public int NewQuantity { get; init; }
    public string Reason { get; init; }
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Checkout event appended to the Redis-backed order stream.
/// </summary>
public record GroceryCheckoutEvent
{
    public GroceryCheckoutEvent(
        string orderId,
        string shopperId,
        string sessionId,
        string saleId,
        int itemCount,
        decimal subtotal,
        DateTime checkedOutAtUtc)
    {
        OrderId = orderId;
        ShopperId = shopperId;
        SessionId = sessionId;
        SaleId = saleId;
        ItemCount = itemCount;
        Subtotal = subtotal;
        CheckedOutAtUtc = checkedOutAtUtc;
    }

    public string OrderId { get; init; }
    public string ShopperId { get; init; }
    public string SessionId { get; init; }
    public string SaleId { get; init; }
    public int ItemCount { get; init; }
    public decimal Subtotal { get; init; }
    public DateTime CheckedOutAtUtc { get; init; }
    public string StoreId { get; init; } = string.Empty;
    public string FulfillmentMethod { get; init; } = string.Empty;
    public string ReceiptStatus { get; init; } = SuperCenterReceiptSearch.ClearedStatus;
    public string ReceiptSearchText { get; init; } = string.Empty;
}

/// <summary>
/// Request issued by the front-door receipt checker.
/// </summary>
public readonly record struct ReceiptExitCheckRequest
{
    public ReceiptExitCheckRequest(
        string orderId,
        string shopperId,
        string storeId,
        DateTime checkedOutAfterUtc,
        int take = 1,
        bool flagForManualReview = false,
        string receiptStatus = SuperCenterReceiptSearch.ClearedStatus)
    {
        OrderId = orderId;
        ShopperId = shopperId;
        StoreId = storeId;
        CheckedOutAfterUtc = checkedOutAfterUtc;
        Take = take;
        FlagForManualReview = flagForManualReview;
        ReceiptStatus = receiptStatus;
    }

    public string OrderId { get; init; }
    public string ShopperId { get; init; }
    public string StoreId { get; init; }
    public DateTime CheckedOutAfterUtc { get; init; }
    public int Take { get; init; }
    public bool FlagForManualReview { get; init; }
    public string ReceiptStatus { get; init; }
}

/// <summary>
/// Provider-level receipt search result.
/// </summary>
public readonly record struct ReceiptSearchLookup(bool Matched, long HitCount, string SearchDocumentKey);

/// <summary>
/// Shared result returned by the front-door receipt checker flow.
/// </summary>
public readonly record struct ReceiptExitCheckResult(
    bool Matched,
    long HitCount,
    long InvalidatedTargets,
    bool FlaggedForManualReview);

/// <summary>
/// HASH-backed RediSearch projection for a completed receipt.
/// </summary>
public sealed record ReceiptSearchDocument
{
    public ReceiptSearchDocument(
        string orderId,
        string shopperId,
        string sessionId,
        string saleId,
        string storeId,
        string receiptStatus,
        string fulfillmentMethod,
        string runScope,
        int itemCount,
        decimal subtotal,
        long checkedOutUnixMilliseconds,
        string searchText)
    {
        OrderId = orderId;
        ShopperId = shopperId;
        SessionId = sessionId;
        SaleId = saleId;
        StoreId = storeId;
        ReceiptStatus = receiptStatus;
        FulfillmentMethod = fulfillmentMethod;
        RunScope = runScope;
        ItemCount = itemCount;
        Subtotal = subtotal;
        CheckedOutUnixMilliseconds = checkedOutUnixMilliseconds;
        SearchText = searchText;
    }

    public string OrderId { get; init; }
    public string ShopperId { get; init; }
    public string SessionId { get; init; }
    public string SaleId { get; init; }
    public string StoreId { get; init; }
    public string ReceiptStatus { get; init; }
    public string FulfillmentMethod { get; init; }
    public string RunScope { get; init; }
    public int ItemCount { get; init; }
    public decimal Subtotal { get; init; }
    public long CheckedOutUnixMilliseconds { get; init; }
    public string SearchText { get; init; }
}
