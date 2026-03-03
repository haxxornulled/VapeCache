using System.Buffers;
using System.Text;
using System.Text.Json;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Console.GroceryStore;

/// <summary>
/// Strict command-for-command parity implementation for head-to-head comparison.
/// Mirrors StackExchange.Redis payload encoding/shape (JSON cart/session payloads).
/// </summary>
public sealed class VapeCacheRawParityGroceryStoreService : IGroceryStoreService, ICartBatchWriter
{
    private readonly IRedisCommandExecutor _redis;
    private readonly string _keyPrefix;
    private static readonly Product[] Products = GroceryStoreService.GetAllProducts();
    private static readonly IReadOnlyDictionary<string, Product> ProductsById = BuildProductMap(Products);
    private static readonly GroceryStoreJsonContext JsonContext = new(new());
    private static readonly Encoding Utf8 = Encoding.UTF8;

    public VapeCacheRawParityGroceryStoreService(IRedisCommandExecutor redis, string? keyPrefix = null)
    {
        _redis = redis;
        _keyPrefix = NormalizeKeyPrefix(keyPrefix);
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<Product?> GetProductAsync(string productId)
    {
        var key = Key($"product:{productId}");
        using var lease = await _redis.GetLeaseAsync(key, CancellationToken.None).ConfigureAwait(false);
        if (!lease.IsNull)
            return JsonSerializer.Deserialize(lease.Span, JsonContext.Product);

        if (!ProductsById.TryGetValue(productId, out var product))
            return null;

        var serialized = JsonSerializer.SerializeToUtf8Bytes(product, JsonContext.Product);
        await _redis.SetAsync(key, serialized, TimeSpan.FromMinutes(10), CancellationToken.None).ConfigureAwait(false);
        return product;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask CacheProductAsync(Product product, TimeSpan ttl)
    {
        var serialized = JsonSerializer.SerializeToUtf8Bytes(product, JsonContext.Product);
        await _redis.SetAsync(Key($"product:{product.Id}"), serialized, ttl, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds value.
    /// </summary>
    public async ValueTask AddToCartAsync(string userId, CartItem item)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(item, JsonContext.CartItem);
        await _redis.RPushAsync(Key($"cart:{userId}"), payload, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds value.
    /// </summary>
    public async ValueTask AddToCartBatchAsync(string userId, IReadOnlyList<CartItem> items)
    {
        if (items.Count == 0)
            return;

        var key = Key($"cart:{userId}");
        var payloads = ArrayPool<ReadOnlyMemory<byte>>.Shared.Rent(items.Count);
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                payloads[i] = JsonSerializer.SerializeToUtf8Bytes(items[i], JsonContext.CartItem);
            }

            try
            {
                await _redis.RPushManyAsync(key, payloads, items.Count, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (NotSupportedException)
            {
                // Fallback for executors that do not support multi-value RPUSH.
            }

            var pending = ArrayPool<Task<long>>.Shared.Rent(items.Count);
            try
            {
                for (var i = 0; i < items.Count; i++)
                {
                    var operation = _redis.RPushAsync(key, payloads[i], CancellationToken.None);
                    pending[i] = operation.IsCompletedSuccessfully
                        ? Task.FromResult(operation.Result)
                        : operation.AsTask();
                }

                for (var i = 0; i < items.Count; i++)
                {
                    await pending[i].ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<Task<long>>.Shared.Return(pending, clearArray: true);
            }
        }
        finally
        {
            ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(payloads, clearArray: true);
        }
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<CartItem[]> GetCartAsync(string userId)
    {
        var values = await _redis.LRangeAsync(Key($"cart:{userId}"), 0, -1, CancellationToken.None).ConfigureAwait(false);
        if (values.Length == 0)
            return Array.Empty<CartItem>();

        var cart = new CartItem[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is null)
            {
                cart[i] = default!;
                continue;
            }

            cart[i] = JsonSerializer.Deserialize(values[i]!.AsSpan(), JsonContext.CartItem)!;
        }

        return cart;
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<long> GetCartCountAsync(string userId)
        => await _redis.LLenAsync(Key($"cart:{userId}"), CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask ClearCartAsync(string userId)
        => await _redis.DeleteAsync(Key($"cart:{userId}"), CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask JoinFlashSaleAsync(string saleId, string userId)
    {
        await ExecuteWithRentedUtf8Async(userId, payload =>
            _redis.SAddAsync(Key($"sale:{saleId}:participants"), payload, CancellationToken.None)).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> IsInFlashSaleAsync(string saleId, string userId)
    {
        return await ExecuteWithRentedUtf8Async(userId, payload =>
            _redis.SIsMemberAsync(Key($"sale:{saleId}:participants"), payload, CancellationToken.None)).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<long> GetFlashSaleParticipantCountAsync(string saleId)
        => await _redis.SCardAsync(Key($"sale:{saleId}:participants"), CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask SaveSessionAsync(string sessionId, UserSession session)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(session, JsonContext.UserSession);
        await _redis.SetAsync(Key($"session:{sessionId}"), payload, TimeSpan.FromHours(1), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<UserSession?> GetSessionAsync(string sessionId)
    {
        using var lease = await _redis.GetLeaseAsync(Key($"session:{sessionId}"), CancellationToken.None).ConfigureAwait(false);
        if (lease.IsNull)
            return null;

        if (SessionBinaryCodec.TryDeserialize(lease.Span, out var binarySession))
            return binarySession;

        return JsonSerializer.Deserialize(lease.Span, JsonContext.UserSession);
    }

    private static IReadOnlyDictionary<string, Product> BuildProductMap(Product[] products)
    {
        var map = new Dictionary<string, Product>(products.Length, StringComparer.Ordinal);
        foreach (var product in products)
            map[product.Id] = product;

        return map;
    }

    private static async ValueTask ExecuteWithRentedUtf8Async(string value, Func<ReadOnlyMemory<byte>, ValueTask> action)
    {
        var byteCount = Utf8.GetByteCount(value);
        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Utf8.GetBytes(value, rented);
            await action(rented.AsMemory(0, written)).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static async ValueTask<T> ExecuteWithRentedUtf8Async<T>(string value, Func<ReadOnlyMemory<byte>, ValueTask<T>> action)
    {
        var byteCount = Utf8.GetByteCount(value);
        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Utf8.GetBytes(value, rented);
            return await action(rented.AsMemory(0, written)).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
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
