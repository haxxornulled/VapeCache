using System.Text.Json;
using StackExchange.Redis;

namespace VapeCache.Console.GroceryStore;

/// <summary>
/// StackExchange.Redis implementation of grocery store operations.
/// Used for head-to-head comparison with VapeCache.
/// </summary>
public class StackExchangeRedisGroceryStoreService : IGroceryStoreService
{
    private readonly IDatabase _db;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public StackExchangeRedisGroceryStoreService(IConnectionMultiplexer redis)
    {
        _db = redis.GetDatabase();
    }

    public async Task<Product?> GetProductAsync(string productId)
    {
        var json = await _db.StringGetAsync($"product:{productId}");
        return json.HasValue ? JsonSerializer.Deserialize<Product>((string)json!) : null;
    }

    public async Task CacheProductAsync(Product product, TimeSpan ttl)
    {
        var json = JsonSerializer.Serialize(product, _jsonOptions);
        await _db.StringSetAsync($"product:{product.Id}", json, ttl);
    }

    public async Task AddToCartAsync(string userId, CartItem item)
    {
        var json = JsonSerializer.Serialize(item, _jsonOptions);
        await _db.ListRightPushAsync($"cart:{userId}", json);
    }

    public async Task<CartItem[]> GetCartAsync(string userId)
    {
        var values = await _db.ListRangeAsync($"cart:{userId}");
        return values.Select(v => JsonSerializer.Deserialize<CartItem>((string)v!)!).ToArray();
    }

    public async Task<long> GetCartCountAsync(string userId)
    {
        return await _db.ListLengthAsync($"cart:{userId}");
    }

    public async Task ClearCartAsync(string userId)
    {
        await _db.KeyDeleteAsync($"cart:{userId}");
    }

    public async Task JoinFlashSaleAsync(string saleId, string userId)
    {
        await _db.SetAddAsync($"sale:{saleId}:participants", userId);
    }

    public async Task<bool> IsInFlashSaleAsync(string saleId, string userId)
    {
        return await _db.SetContainsAsync($"sale:{saleId}:participants", userId);
    }

    public async Task<long> GetFlashSaleParticipantCountAsync(string saleId)
    {
        return await _db.SetLengthAsync($"sale:{saleId}:participants");
    }

    public async Task SaveSessionAsync(string sessionId, UserSession session)
    {
        var json = JsonSerializer.Serialize(session, _jsonOptions);
        await _db.StringSetAsync($"session:{sessionId}", json, TimeSpan.FromHours(1));
    }

    public async Task<UserSession?> GetSessionAsync(string sessionId)
    {
        var json = await _db.StringGetAsync($"session:{sessionId}");
        return json.HasValue ? JsonSerializer.Deserialize<UserSession>((string)json!) : null;
    }
}

/// <summary>
/// Interface that both VapeCache and StackExchange.Redis implementations will follow.
/// </summary>
public interface IGroceryStoreService
{
    Task<Product?> GetProductAsync(string productId);
    Task CacheProductAsync(Product product, TimeSpan ttl);
    Task AddToCartAsync(string userId, CartItem item);
    Task<CartItem[]> GetCartAsync(string userId);
    Task<long> GetCartCountAsync(string userId);
    Task ClearCartAsync(string userId);
    Task JoinFlashSaleAsync(string saleId, string userId);
    Task<bool> IsInFlashSaleAsync(string saleId, string userId);
    Task<long> GetFlashSaleParticipantCountAsync(string saleId);
    Task SaveSessionAsync(string sessionId, UserSession session);
    Task<UserSession?> GetSessionAsync(string sessionId);
}
