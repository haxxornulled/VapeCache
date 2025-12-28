namespace VapeCache.Console.GroceryStore;

/// <summary>
/// Product in the grocery store inventory
/// </summary>
public record Product(
    string Id,
    string Name,
    string Category,
    decimal Price,
    int StockQuantity,
    string ImageUrl);

/// <summary>
/// Shopping cart item
/// </summary>
public record CartItem(
    string ProductId,
    string ProductName,
    decimal Price,
    int Quantity,
    DateTime AddedAt);

/// <summary>
/// User session data (active cart, preferences, etc.)
/// </summary>
public record UserSession(
    string UserId,
    string SessionId,
    DateTime CreatedAt,
    DateTime LastActivityAt,
    string[] RecentlyViewedProductIds,
    string? ActiveCartId);

/// <summary>
/// Flash sale event with limited inventory
/// </summary>
public record FlashSale(
    string Id,
    string ProductId,
    string ProductName,
    decimal OriginalPrice,
    decimal SalePrice,
    int TotalQuantity,
    int RemainingQuantity,
    DateTime StartsAt,
    DateTime EndsAt);

/// <summary>
/// Real-time inventory update event
/// </summary>
public record InventoryUpdate(
    string ProductId,
    int OldQuantity,
    int NewQuantity,
    string Reason,
    DateTime Timestamp);
