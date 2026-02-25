using System.Globalization;
using System.Text;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Buffers;
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
    private static readonly IReadOnlyDictionary<string, int> ProductIndexById = BuildProductIndexMap(Products);
    private static readonly Encoding Utf8 = Encoding.UTF8;
    private static readonly GroceryStoreJsonContext JsonContext = new(new());
    private const uint OptimizedCartFormatMagicV1 = 0x56434331; // "VCC1"
    private const uint OptimizedCartFormatMagicV2 = 0x56434332; // "VCC2"

    public VapeCacheRawGroceryStoreService(IRedisCommandExecutor redis)
    {
        _redis = redis;
    }

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

    public async ValueTask CacheProductAsync(Product product, TimeSpan ttl)
    {
        var key = $"product:{product.Id}";
        var serialized = Serialize(product);
        await _redis.SetAsync(key, serialized, ttl, CancellationToken.None);
    }

    public async ValueTask AddToCartAsync(string userId, CartItem item)
    {
        var key = $"cart:{userId}";
        var serialized = Serialize(item);
        await _redis.RPushAsync(key, serialized, CancellationToken.None);
    }

    public async ValueTask AddToCartBatchAsync(string userId, IReadOnlyList<CartItem> items)
    {
        if (items.Count == 0)
            return;

        var optimizedCartKey = GetOptimizedCartKey(userId);
        var optimizedCountKey = GetOptimizedCartCountKey(userId);
        var cartPayload = SerializeCartItems(items);
        var countPayload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(countPayload, items.Count);

        var setCart = _redis.SetAsync(optimizedCartKey, cartPayload, TimeSpan.FromMinutes(15), CancellationToken.None);
        var setCount = _redis.SetAsync(optimizedCountKey, countPayload, TimeSpan.FromMinutes(15), CancellationToken.None);
        if (setCart.IsCompletedSuccessfully)
        {
            _ = setCart.Result;
        }
        else
        {
            await setCart.ConfigureAwait(false);
        }

        if (setCount.IsCompletedSuccessfully)
        {
            _ = setCount.Result;
        }
        else
        {
            await setCount.ConfigureAwait(false);
        }
    }

    public async ValueTask<CartItem[]> GetCartAsync(string userId)
    {
        using var optimized = await _redis.GetLeaseAsync(GetOptimizedCartKey(userId), CancellationToken.None);
        if (!optimized.IsNull)
        {
            return DeserializeCartItemsOptimized(optimized.Span);
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

    public async ValueTask<long> GetCartCountAsync(string userId)
    {
        using var optimizedCount = await _redis.GetLeaseAsync(GetOptimizedCartCountKey(userId), CancellationToken.None);
        if (!optimizedCount.IsNull && optimizedCount.Length == 4)
        {
            return BinaryPrimitives.ReadInt32LittleEndian(optimizedCount.Span);
        }
        if (!optimizedCount.IsNull && long.TryParse(
                optimizedCount.Span,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var count))
        {
            return count;
        }

        return await _redis.LLenAsync($"cart:{userId}", CancellationToken.None);
    }

    public async ValueTask ClearCartAsync(string userId)
    {
        var deleteList = _redis.DeleteAsync($"cart:{userId}", CancellationToken.None);
        var deleteOptimized = _redis.DeleteAsync(GetOptimizedCartKey(userId), CancellationToken.None);
        var deleteCount = _redis.DeleteAsync(GetOptimizedCartCountKey(userId), CancellationToken.None);

        if (deleteList.IsCompletedSuccessfully)
        {
            _ = deleteList.Result;
        }
        else
        {
            await deleteList.ConfigureAwait(false);
        }

        if (deleteOptimized.IsCompletedSuccessfully)
        {
            _ = deleteOptimized.Result;
        }
        else
        {
            await deleteOptimized.ConfigureAwait(false);
        }

        if (deleteCount.IsCompletedSuccessfully)
        {
            _ = deleteCount.Result;
        }
        else
        {
            await deleteCount.ConfigureAwait(false);
        }
    }

    public async ValueTask JoinFlashSaleAsync(string saleId, string userId)
    {
        await ExecuteWithRentedUtf8Async(userId, payload =>
            _redis.SAddAsync($"sale:{saleId}:participants", payload, CancellationToken.None)).ConfigureAwait(false);
    }

    public async ValueTask<bool> IsInFlashSaleAsync(string saleId, string userId)
    {
        return await ExecuteWithRentedUtf8Async(userId, payload =>
            _redis.SIsMemberAsync($"sale:{saleId}:participants", payload, CancellationToken.None)).ConfigureAwait(false);
    }

    public async ValueTask<long> GetFlashSaleParticipantCountAsync(string saleId)
    {
        return await _redis.SCardAsync($"sale:{saleId}:participants", CancellationToken.None);
    }

    public async ValueTask SaveSessionAsync(string sessionId, UserSession session)
    {
        var serialized = SessionBinaryCodec.Serialize(session);
        await _redis.SetAsync($"session:{sessionId}", serialized, TimeSpan.FromHours(1), CancellationToken.None);
    }

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
            Product product => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(product, JsonContext.Product),
            CartItem cartItem => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(cartItem, JsonContext.CartItem),
            UserSession userSession => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(userSession, JsonContext.UserSession),
            _ => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value)
        };
    }

    private static T Deserialize<T>(ReadOnlySpan<byte> bytes)
    {
        if (typeof(T) == typeof(Product))
            return (T)(object)(System.Text.Json.JsonSerializer.Deserialize(bytes, JsonContext.Product)!);
        if (typeof(T) == typeof(CartItem[]))
            return (T)(object)(System.Text.Json.JsonSerializer.Deserialize(bytes, JsonContext.CartItemArray)!);
        if (typeof(T) == typeof(UserSession))
            return (T)(object)(System.Text.Json.JsonSerializer.Deserialize(bytes, JsonContext.UserSession)!);

        return System.Text.Json.JsonSerializer.Deserialize<T>(bytes)!;
    }

    private static ReadOnlyMemory<byte> SerializeCartItems(IReadOnlyList<CartItem> items)
    {
        var cartArray = items as CartItem[] ?? items.ToArray();
        return SerializeCartItemsCompact(cartArray);
    }

    private static CartItem[] DeserializeCartItemsOptimized(ReadOnlySpan<byte> payload)
    {
        if (payload.Length >= 8)
        {
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, 4));
            if (magic == OptimizedCartFormatMagicV2)
                return DeserializeCartItemsCompactV2(payload);
            if (magic == OptimizedCartFormatMagicV1)
                return DeserializeCartItemsCompactV1(payload);
        }

        return Deserialize<CartItem[]>(payload);
    }

    private static byte[] SerializeCartItemsCompact(CartItem[] items)
    {
        var totalSize = 8; // magic + count
        for (var i = 0; i < items.Length; i++)
        {
            totalSize += 2; // product index (ushort)
            totalSize += 2; // quantity (ushort)
            totalSize += 8; // ticks
        }

        var buffer = new byte[totalSize];
        var span = buffer.AsSpan();
        var offset = 0;

        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, 4), OptimizedCartFormatMagicV2);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), items.Length);
        offset += 4;

        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            if (!ProductIndexById.TryGetValue(item.ProductId, out var productIndex))
                productIndex = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, 2), (ushort)productIndex);
            offset += 2;

            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, 2), (ushort)Math.Clamp(item.Quantity, 0, ushort.MaxValue));
            offset += 2;

            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset, 8), item.AddedAt.Ticks);
            offset += 8;
        }

        return buffer;
    }

    private static CartItem[] DeserializeCartItemsCompactV2(ReadOnlySpan<byte> payload)
    {
        var span = payload;
        if (span.Length < 8)
            return Array.Empty<CartItem>();

        var offset = 4; // magic already validated by caller
        var count = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;
        if (count <= 0)
            return Array.Empty<CartItem>();

        var items = new CartItem[count];
        for (var i = 0; i < count; i++)
        {
            var productIndex = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
            offset += 2;
            var quantity = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
            offset += 2;
            var ticks = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset, 8));
            offset += 8;

            var product = (productIndex < Products.Length) ? Products[productIndex] : Products[0];
            items[i] = new CartItem(
                product.Id,
                product.Name,
                product.Price,
                quantity,
                new DateTime(ticks, DateTimeKind.Utc));
        }

        return items;
    }

    private static CartItem[] DeserializeCartItemsCompactV1(ReadOnlySpan<byte> payload)
    {
        var span = payload;
        if (span.Length < 8)
            return Array.Empty<CartItem>();

        var offset = 4; // magic already validated by caller
        var count = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;
        if (count <= 0)
            return Array.Empty<CartItem>();

        var items = new CartItem[count];
        for (var i = 0; i < count; i++)
        {
            var productIdLen = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
            offset += 4;
            var productId = Utf8.GetString(span.Slice(offset, productIdLen));
            offset += productIdLen;

            var productNameLen = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
            offset += 4;
            var productName = Utf8.GetString(span.Slice(offset, productNameLen));
            offset += productNameLen;

            var priceCents = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
            offset += 4;
            var quantity = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
            offset += 4;
            var ticks = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset, 8));
            offset += 8;

            items[i] = new CartItem(
                productId,
                productName,
                priceCents / 100m,
                quantity,
                new DateTime(ticks, DateTimeKind.Utc));
        }

        return items;
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
        {
            map[products[i].Id] = i;
        }

        return map;
    }

    private static string GetOptimizedCartKey(string userId) => $"cart:optimized:{userId}";

    private static string GetOptimizedCartCountKey(string userId) => $"cart:optimized:{userId}:count";
}
