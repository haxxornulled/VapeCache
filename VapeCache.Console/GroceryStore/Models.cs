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
