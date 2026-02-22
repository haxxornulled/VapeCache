using System.Text.Json;
using System.Globalization;
using System.Text;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Console.GroceryStore;

/// <summary>
/// Low-level VapeCache implementation for head-to-head benchmarking.
/// Uses IRedisCommandExecutor directly to minimize abstraction overhead.
/// </summary>
public sealed class VapeCacheRawGroceryStoreService : IGroceryStoreService, ICartBatchWriter
{
    private readonly IRedisCommandExecutor _redis;
    private static readonly Product[] Products = GroceryStoreService.GetAllProducts();
    private static readonly IReadOnlyDictionary<string, Product> ProductsById = BuildProductMap(Products);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public VapeCacheRawGroceryStoreService(IRedisCommandExecutor redis)
    {
        _redis = redis;
    }

    public async Task<Product?> GetProductAsync(string productId)
    {
        var key = $"product:{productId}";
        var bytes = await _redis.GetAsync(key, CancellationToken.None);
        if (bytes is not null)
        {
            return Deserialize<Product>(bytes);
        }

        if (!ProductsById.TryGetValue(productId, out var product))
        {
            return null;
        }

        var serialized = Serialize(product);
        await _redis.SetAsync(key, serialized, TimeSpan.FromMinutes(10), CancellationToken.None);
        return product;
    }

    public async Task CacheProductAsync(Product product, TimeSpan ttl)
    {
        var key = $"product:{product.Id}";
        var serialized = Serialize(product);
        await _redis.SetAsync(key, serialized, ttl, CancellationToken.None);
    }

    public async Task AddToCartAsync(string userId, CartItem item)
    {
        var key = $"cart:{userId}";
        var serialized = Serialize(item);
        await _redis.RPushAsync(key, serialized, CancellationToken.None);
    }

    public async Task AddToCartBatchAsync(string userId, IReadOnlyList<CartItem> items)
    {
        if (items.Count == 0)
            return;

        var optimizedCartKey = GetOptimizedCartKey(userId);
        var optimizedCountKey = GetOptimizedCartCountKey(userId);
        var cartPayload = Serialize(items);
        var countPayload = Encoding.UTF8.GetBytes(items.Count.ToString(CultureInfo.InvariantCulture));

        await using var batch = _redis.CreateBatch();
        _ = batch.QueueAsync((executor, ct) =>
            executor.SetAsync(optimizedCartKey, cartPayload, TimeSpan.FromMinutes(15), ct), CancellationToken.None);
        _ = batch.QueueAsync((executor, ct) =>
            executor.SetAsync(optimizedCountKey, countPayload, TimeSpan.FromMinutes(15), ct), CancellationToken.None);
        await batch.ExecuteAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<CartItem[]> GetCartAsync(string userId)
    {
        var optimized = await _redis.GetAsync(GetOptimizedCartKey(userId), CancellationToken.None);
        if (optimized is not null)
        {
            return Deserialize<CartItem[]>(optimized);
        }

        var values = await _redis.LRangeAsync($"cart:{userId}", 0, -1, CancellationToken.None);
        if (values.Length == 0)
        {
            return Array.Empty<CartItem>();
        }

        var cart = new CartItem[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            cart[i] = values[i] is null ? default! : Deserialize<CartItem>(values[i]!);
        }

        return cart;
    }

    public async Task<long> GetCartCountAsync(string userId)
    {
        var optimizedCount = await _redis.GetAsync(GetOptimizedCartCountKey(userId), CancellationToken.None);
        if (optimizedCount is not null && long.TryParse(
                Encoding.UTF8.GetString(optimizedCount),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var count))
        {
            return count;
        }

        return await _redis.LLenAsync($"cart:{userId}", CancellationToken.None);
    }

    public async Task ClearCartAsync(string userId)
    {
        await using var batch = _redis.CreateBatch();
        _ = batch.QueueAsync((executor, ct) => executor.DeleteAsync($"cart:{userId}", ct), CancellationToken.None);
        _ = batch.QueueAsync((executor, ct) => executor.DeleteAsync(GetOptimizedCartKey(userId), ct), CancellationToken.None);
        _ = batch.QueueAsync((executor, ct) => executor.DeleteAsync(GetOptimizedCartCountKey(userId), ct), CancellationToken.None);
        await batch.ExecuteAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async Task JoinFlashSaleAsync(string saleId, string userId)
    {
        await _redis.SAddAsync($"sale:{saleId}:participants", Serialize(userId), CancellationToken.None);
    }

    public async Task<bool> IsInFlashSaleAsync(string saleId, string userId)
    {
        return await _redis.SIsMemberAsync($"sale:{saleId}:participants", Serialize(userId), CancellationToken.None);
    }

    public async Task<long> GetFlashSaleParticipantCountAsync(string saleId)
    {
        return await _redis.SCardAsync($"sale:{saleId}:participants", CancellationToken.None);
    }

    public async Task SaveSessionAsync(string sessionId, UserSession session)
    {
        var serialized = Serialize(session);
        await _redis.SetAsync($"session:{sessionId}", serialized, TimeSpan.FromHours(1), CancellationToken.None);
    }

    public async Task<UserSession?> GetSessionAsync(string sessionId)
    {
        var bytes = await _redis.GetAsync($"session:{sessionId}", CancellationToken.None);
        return bytes is null ? null : Deserialize<UserSession>(bytes);
    }

    private static ReadOnlyMemory<byte> Serialize<T>(T value)
    {
        return JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
    }

    private static T Deserialize<T>(byte[] bytes)
    {
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions)!;
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

    private static string GetOptimizedCartKey(string userId) => $"cart:optimized:{userId}";

    private static string GetOptimizedCartCountKey(string userId) => $"cart:optimized:{userId}:count";
}
