using System.Text.Json;
using StackExchange.Redis;

namespace VapeCache.Console.GroceryStore;

/// <summary>
/// StackExchange.Redis implementation of grocery store operations.
/// Used for head-to-head comparison with VapeCache.
/// </summary>
public class StackExchangeRedisGroceryStoreService : IGroceryStoreService, ICartBatchWriter
    , IGroceryStoreComparisonTelemetrySource
{
    private readonly IDatabase _db;
    private readonly string _keyPrefix;
    private static readonly GroceryStoreJsonContext JsonContext = new(new());
    private long _productReadOps;
    private long _productWriteOps;
    private long _cartReadOps;
    private long _cartCountReadOps;
    private long _cartItemWriteOps;
    private long _cartClearWriteOps;
    private long _flashSaleJoinWriteOps;
    private long _flashSaleMembershipReadOps;
    private long _flashSaleParticipantCountReadOps;
    private long _sessionReadOps;
    private long _sessionWriteOps;

    public StackExchangeRedisGroceryStoreService(IConnectionMultiplexer redis, string? keyPrefix = null)
    {
        _db = redis.GetDatabase();
        _keyPrefix = NormalizeKeyPrefix(keyPrefix);
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<Product?> GetProductAsync(string productId)
    {
        Interlocked.Increment(ref _productReadOps);
        var value = await _db.StringGetAsync(Key($"product:{productId}"));
        if (!value.HasValue)
            return null;
        return JsonSerializer.Deserialize((byte[])value!, JsonContext.Product);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask CacheProductAsync(Product product, TimeSpan ttl)
    {
        Interlocked.Increment(ref _productWriteOps);
        var payload = JsonSerializer.SerializeToUtf8Bytes(product, JsonContext.Product);
        return new ValueTask(_db.StringSetAsync(Key($"product:{product.Id}"), payload, ttl));
    }

    /// <summary>
    /// Adds value.
    /// </summary>
    public ValueTask AddToCartAsync(string userId, CartItem item)
    {
        Interlocked.Increment(ref _cartItemWriteOps);
        var payload = JsonSerializer.SerializeToUtf8Bytes(item, JsonContext.CartItem);
        return new ValueTask(_db.ListRightPushAsync(Key($"cart:{userId}"), payload));
    }

    /// <summary>
    /// Adds value.
    /// </summary>
    public async ValueTask AddToCartBatchAsync(string userId, IReadOnlyList<CartItem> items)
    {
        if (items.Count == 0)
            return;
        Interlocked.Add(ref _cartItemWriteOps, items.Count);

        var batch = _db.CreateBatch();
        var key = Key($"cart:{userId}");
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
        Interlocked.Increment(ref _cartReadOps);
        var values = await _db.ListRangeAsync(Key($"cart:{userId}"));
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
        Interlocked.Increment(ref _cartCountReadOps);
        return new ValueTask<long>(_db.ListLengthAsync(Key($"cart:{userId}")));
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask ClearCartAsync(string userId)
    {
        Interlocked.Increment(ref _cartClearWriteOps);
        return new ValueTask(_db.KeyDeleteAsync(Key($"cart:{userId}")));
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask JoinFlashSaleAsync(string saleId, string userId)
    {
        Interlocked.Increment(ref _flashSaleJoinWriteOps);
        return new ValueTask(_db.SetAddAsync(Key($"sale:{saleId}:participants"), userId));
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<bool> IsInFlashSaleAsync(string saleId, string userId)
    {
        Interlocked.Increment(ref _flashSaleMembershipReadOps);
        return new ValueTask<bool>(_db.SetContainsAsync(Key($"sale:{saleId}:participants"), userId));
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public ValueTask<long> GetFlashSaleParticipantCountAsync(string saleId)
    {
        Interlocked.Increment(ref _flashSaleParticipantCountReadOps);
        return new ValueTask<long>(_db.SetLengthAsync(Key($"sale:{saleId}:participants")));
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask SaveSessionAsync(string sessionId, UserSession session)
    {
        Interlocked.Increment(ref _sessionWriteOps);
        var payload = JsonSerializer.SerializeToUtf8Bytes(session, JsonContext.UserSession);
        return new ValueTask(_db.StringSetAsync(Key($"session:{sessionId}"), payload, TimeSpan.FromHours(1)));
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<UserSession?> GetSessionAsync(string sessionId)
    {
        Interlocked.Increment(ref _sessionReadOps);
        var value = await _db.StringGetAsync(Key($"session:{sessionId}"));
        if (!value.HasValue)
            return null;
        return JsonSerializer.Deserialize((byte[])value!, JsonContext.UserSession);
    }

    public GroceryStoreComparisonTelemetrySnapshot GetTelemetrySnapshot()
    {
        return new GroceryStoreComparisonTelemetrySnapshot(
            ProductReadOps: Volatile.Read(ref _productReadOps),
            ProductWriteOps: Volatile.Read(ref _productWriteOps),
            CartReadOps: Volatile.Read(ref _cartReadOps),
            CartCountReadOps: Volatile.Read(ref _cartCountReadOps),
            CartItemWriteOps: Volatile.Read(ref _cartItemWriteOps),
            CartClearWriteOps: Volatile.Read(ref _cartClearWriteOps),
            FlashSaleJoinWriteOps: Volatile.Read(ref _flashSaleJoinWriteOps),
            FlashSaleMembershipReadOps: Volatile.Read(ref _flashSaleMembershipReadOps),
            FlashSaleParticipantCountReadOps: Volatile.Read(ref _flashSaleParticipantCountReadOps),
            SessionReadOps: Volatile.Read(ref _sessionReadOps),
            SessionWriteOps: Volatile.Read(ref _sessionWriteOps));
    }

    private string Key(string suffix)
        => _keyPrefix.Length == 0 ? suffix : string.Concat(_keyPrefix, suffix);

    private static string NormalizeKeyPrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return string.Empty;

        var trimmed = prefix.Trim();
        return trimmed.EndsWith(':') ? trimmed : string.Concat(trimmed, ":");
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
