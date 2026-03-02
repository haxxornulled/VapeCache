using System.Text.Json;
using StackExchange.Redis;

namespace VapeCache.Console.GroceryStore;

/// <summary>
/// StackExchange.Redis implementation of grocery store operations.
/// Used for head-to-head comparison with VapeCache.
/// </summary>
public class StackExchangeRedisGroceryStoreService : IGroceryStoreService, ICartBatchWriter
{
    private readonly IDatabase _db;
    private static readonly GroceryStoreJsonContext JsonContext = new(new());

    public StackExchangeRedisGroceryStoreService(IConnectionMultiplexer redis)
    {
        _db = redis.GetDatabase();
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<Product?> GetProductAsync(string productId)
    {
        var value = await _db.StringGetAsync($"product:{productId}");
        if (!value.HasValue)
            return null;
        return JsonSerializer.Deserialize((byte[])value!, JsonContext.Product);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask CacheProductAsync(Product product, TimeSpan ttl)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(product, JsonContext.Product);
        return new ValueTask(_db.StringSetAsync($"product:{product.Id}", payload, ttl));
    }

    /// <summary>
    /// Adds value.
    /// </summary>
    public ValueTask AddToCartAsync(string userId, CartItem item)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(item, JsonContext.CartItem);
        return new ValueTask(_db.ListRightPushAsync($"cart:{userId}", payload));
    }

    /// <summary>
    /// Adds value.
    /// </summary>
    public async ValueTask AddToCartBatchAsync(string userId, IReadOnlyList<CartItem> items)
    {
        if (items.Count == 0)
            return;

        var batch = _db.CreateBatch();
        var key = $"cart:{userId}";
        var operations = new ValueTask<long>[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(items[i], JsonContext.CartItem);
            operations[i] = new ValueTask<long>(batch.ListRightPushAsync(key, payload));
        }

        batch.Execute();
        foreach (var operation in operations)
        {
            await operation.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<CartItem[]> GetCartAsync(string userId)
    {
        var values = await _db.ListRangeAsync($"cart:{userId}");
        if (values.Length == 0)
            return Array.Empty<CartItem>();

        var items = new CartItem[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            items[i] = JsonSerializer.Deserialize((byte[])values[i]!, JsonContext.CartItem)!;
        }
        return items;
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public ValueTask<long> GetCartCountAsync(string userId)
    {
        return new ValueTask<long>(_db.ListLengthAsync($"cart:{userId}"));
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask ClearCartAsync(string userId)
    {
        return new ValueTask(_db.KeyDeleteAsync($"cart:{userId}"));
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask JoinFlashSaleAsync(string saleId, string userId)
    {
        return new ValueTask(_db.SetAddAsync($"sale:{saleId}:participants", userId));
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<bool> IsInFlashSaleAsync(string saleId, string userId)
    {
        return new ValueTask<bool>(_db.SetContainsAsync($"sale:{saleId}:participants", userId));
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public ValueTask<long> GetFlashSaleParticipantCountAsync(string saleId)
    {
        return new ValueTask<long>(_db.SetLengthAsync($"sale:{saleId}:participants"));
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask SaveSessionAsync(string sessionId, UserSession session)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(session, JsonContext.UserSession);
        return new ValueTask(_db.StringSetAsync($"session:{sessionId}", payload, TimeSpan.FromHours(1)));
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<UserSession?> GetSessionAsync(string sessionId)
    {
        var value = await _db.StringGetAsync($"session:{sessionId}");
        if (!value.HasValue)
            return null;
        return JsonSerializer.Deserialize((byte[])value!, JsonContext.UserSession);
    }
}

/// <summary>
/// Interface that both VapeCache and StackExchange.Redis implementations will follow.
/// </summary>
public interface IGroceryStoreService
{
    ValueTask<Product?> GetProductAsync(string productId);
    ValueTask CacheProductAsync(Product product, TimeSpan ttl);
    ValueTask AddToCartAsync(string userId, CartItem item);
    ValueTask<CartItem[]> GetCartAsync(string userId);
    ValueTask<long> GetCartCountAsync(string userId);
    ValueTask ClearCartAsync(string userId);
    ValueTask JoinFlashSaleAsync(string saleId, string userId);
    ValueTask<bool> IsInFlashSaleAsync(string saleId, string userId);
    ValueTask<long> GetFlashSaleParticipantCountAsync(string saleId);
    ValueTask SaveSessionAsync(string sessionId, UserSession session);
    ValueTask<UserSession?> GetSessionAsync(string sessionId);
}

/// <summary>
/// Optional fast-path for batching cart writes in stress tests.
/// </summary>
public interface ICartBatchWriter
{
    ValueTask AddToCartBatchAsync(string userId, IReadOnlyList<CartItem> items);
}
