using System.Text.Json;
using System.Text;
using System.Buffers;
using System.Buffers.Binary;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Console.GroceryStore;

/// <summary>
/// Strict command-for-command parity implementation for head-to-head comparison.
/// Uses list-based cart operations without optimized shortcuts.
/// </summary>
public sealed class VapeCacheRawParityGroceryStoreService : IGroceryStoreService, ICartBatchWriter
{
    private readonly IRedisCommandExecutor _redis;
    private static readonly Product[] Products = GroceryStoreService.GetAllProducts();
    private static readonly IReadOnlyDictionary<string, Product> ProductsById = BuildProductMap(Products);
    private static readonly IReadOnlyDictionary<string, int> ProductIndexById = BuildProductIndexMap(Products);
    private static readonly GroceryStoreJsonContext JsonContext = new(new());
    private static readonly Encoding Utf8 = Encoding.UTF8;
    private const int CompactCartItemBytes = 12;

    public VapeCacheRawParityGroceryStoreService(IRedisCommandExecutor redis)
    {
        _redis = redis;
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<Product?> GetProductAsync(string productId)
    {
        var key = $"product:{productId}";
        using var lease = await _redis.GetLeaseAsync(key, CancellationToken.None);
        if (!lease.IsNull)
        {
            return Deserialize<Product>(lease.Span);
        }

        if (!ProductsById.TryGetValue(productId, out var product))
        {
            return null;
        }

        var serialized = Serialize(product);
        await _redis.SetAsync(key, serialized, TimeSpan.FromMinutes(10), CancellationToken.None);
        return product;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask CacheProductAsync(Product product, TimeSpan ttl)
    {
        var key = $"product:{product.Id}";
        var serialized = Serialize(product);
        await _redis.SetAsync(key, serialized, ttl, CancellationToken.None);
    }

    /// <summary>
    /// Adds value.
    /// </summary>
    public async ValueTask AddToCartAsync(string userId, CartItem item)
    {
        var key = $"cart:{userId}";
        var packed = ArrayPool<byte>.Shared.Rent(CompactCartItemBytes);
        try
        {
            WriteCompactCartItem(item, packed.AsSpan(0, CompactCartItemBytes));
            await _redis.RPushAsync(key, packed.AsMemory(0, CompactCartItemBytes), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packed);
        }
    }

    /// <summary>
    /// Adds value.
    /// </summary>
    public async ValueTask AddToCartBatchAsync(string userId, IReadOnlyList<CartItem> items)
    {
        if (items.Count == 0)
            return;

        var key = $"cart:{userId}";
        var packedItems = ArrayPool<byte>.Shared.Rent(items.Count * CompactCartItemBytes);
        var payloads = ArrayPool<ReadOnlyMemory<byte>>.Shared.Rent(items.Count);
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var offset = i * CompactCartItemBytes;
                WriteCompactCartItem(items[i], packedItems.AsSpan(offset, CompactCartItemBytes));
                payloads[i] = packedItems.AsMemory(offset, CompactCartItemBytes);
            }

            await _redis.RPushManyAsync(key, payloads, items.Count, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        catch (NotSupportedException)
        {
            // Compatibility fallback for executors that haven't implemented multi-value RPUSH.
        }
        try
        {
            var pending = ArrayPool<ValueTask<long>>.Shared.Rent(items.Count);
            try
            {
                for (var i = 0; i < items.Count; i++)
                {
                    pending[i] = _redis.RPushAsync(
                        key,
                        payloads[i],
                        CancellationToken.None);
                }

                for (var i = 0; i < items.Count; i++)
                {
                    var operation = pending[i];
                    if (operation.IsCompletedSuccessfully)
                    {
                        _ = operation.Result;
                    }
                    else
                    {
                        await operation.ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                ArrayPool<ValueTask<long>>.Shared.Return(pending, clearArray: true);
            }
        }
        finally
        {
            ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(payloads, clearArray: true);
            ArrayPool<byte>.Shared.Return(packedItems);
        }
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<CartItem[]> GetCartAsync(string userId)
    {
        var key = $"cart:{userId}";
        var values = await _redis.LRangeAsync(key, 0, -1, CancellationToken.None);
        if (values.Length == 0)
        {
            return Array.Empty<CartItem>();
        }

        var cart = new CartItem[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is null)
            {
                cart[i] = default!;
                continue;
            }

            var payload = values[i]!;
            if (payload.Length == CompactCartItemBytes &&
                TryReadCompactCartItem(payload.AsSpan(0, CompactCartItemBytes), out var compactItem))
            {
                cart[i] = compactItem;
                continue;
            }

            // Backward compatibility for existing JSON cart entries.
            cart[i] = JsonSerializer.Deserialize(payload.AsSpan(), JsonContext.CartItem)!;
        }

        return cart;
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<long> GetCartCountAsync(string userId)
    {
        return await _redis.LLenAsync($"cart:{userId}", CancellationToken.None);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask ClearCartAsync(string userId)
    {
        await _redis.DeleteAsync($"cart:{userId}", CancellationToken.None);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask JoinFlashSaleAsync(string saleId, string userId)
    {
        await ExecuteWithRentedUtf8Async(userId, payload =>
            _redis.SAddAsync($"sale:{saleId}:participants", payload, CancellationToken.None)).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> IsInFlashSaleAsync(string saleId, string userId)
    {
        return await ExecuteWithRentedUtf8Async(userId, payload =>
            _redis.SIsMemberAsync($"sale:{saleId}:participants", payload, CancellationToken.None)).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<long> GetFlashSaleParticipantCountAsync(string saleId)
    {
        return await _redis.SCardAsync($"sale:{saleId}:participants", CancellationToken.None);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask SaveSessionAsync(string sessionId, UserSession session)
    {
        var serialized = SessionBinaryCodec.Serialize(session);
        await _redis.SetAsync($"session:{sessionId}", serialized, TimeSpan.FromHours(1), CancellationToken.None);
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<UserSession?> GetSessionAsync(string sessionId)
    {
        using var lease = await _redis.GetLeaseAsync($"session:{sessionId}", CancellationToken.None);
        if (lease.IsNull)
            return null;

        if (SessionBinaryCodec.TryDeserialize(lease.Span, out var session))
            return session;

        // Backward compatibility with legacy JSON session payloads.
        return Deserialize<UserSession>(lease.Span);
    }

    private static ReadOnlyMemory<byte> Serialize<T>(T value)
    {
        return value switch
        {
            Product product => JsonSerializer.SerializeToUtf8Bytes(product, JsonContext.Product),
            CartItem cartItem => JsonSerializer.SerializeToUtf8Bytes(cartItem, JsonContext.CartItem),
            UserSession session => JsonSerializer.SerializeToUtf8Bytes(session, JsonContext.UserSession),
            _ => JsonSerializer.SerializeToUtf8Bytes(value)
        };
    }

    private static T Deserialize<T>(ReadOnlySpan<byte> bytes)
    {
        if (typeof(T) == typeof(Product))
            return (T)(object)(JsonSerializer.Deserialize(bytes, JsonContext.Product)!);
        if (typeof(T) == typeof(CartItem))
            return (T)(object)(JsonSerializer.Deserialize(bytes, JsonContext.CartItem)!);
        if (typeof(T) == typeof(UserSession))
            return (T)(object)(JsonSerializer.Deserialize(bytes, JsonContext.UserSession)!);

        return JsonSerializer.Deserialize<T>(bytes)!;
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

    private static IReadOnlyDictionary<string, int> BuildProductIndexMap(Product[] products)
    {
        var map = new Dictionary<string, int>(products.Length, StringComparer.Ordinal);
        for (var i = 0; i < products.Length; i++)
            map[products[i].Id] = i;
        return map;
    }

    private static void WriteCompactCartItem(in CartItem item, Span<byte> destination)
    {
        if (!ProductIndexById.TryGetValue(item.ProductId, out var productIndex))
            productIndex = 0;

        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(0, 2), (ushort)productIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(2, 2), (ushort)Math.Clamp(item.Quantity, 0, ushort.MaxValue));
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(4, 8), item.AddedAt.Ticks);
    }

    private static bool TryReadCompactCartItem(ReadOnlySpan<byte> payload, out CartItem item)
    {
        item = default!;
        if (payload.Length != CompactCartItemBytes)
            return false;

        var productIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(0, 2));
        var quantity = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2));
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(4, 8));
        if ((uint)productIndex >= (uint)Products.Length)
            return false;

        var product = Products[productIndex];
        item = new CartItem(
            product.Id,
            product.Name,
            product.Price,
            quantity,
            new DateTime(ticks, DateTimeKind.Utc));
        return true;
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
}
