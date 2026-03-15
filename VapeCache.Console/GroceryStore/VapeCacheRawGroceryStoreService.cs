using System.Globalization;
using System.Text;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Buffers;
using System.Collections.Concurrent;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Console.GroceryStore;

/// <summary>
/// Low-level VapeCache implementation for head-to-head benchmarking.
/// Uses IRedisCommandExecutor directly to minimize abstraction overhead.
/// </summary>
public sealed class VapeCacheRawGroceryStoreService : IGroceryStoreService, ICartBatchWriter
    , IGroceryStoreComparisonTelemetrySource
{
    private readonly IRedisCommandExecutor _redis;
    private readonly string _keyPrefix;
    private readonly bool _optimizedCleanupOnly;
    private readonly ConcurrentDictionary<string, long>? _flashSaleCounts;
    private static readonly Product[] Products = GroceryStoreService.GetAllProducts();
    private static readonly IReadOnlyDictionary<string, Product> ProductsById = BuildProductMap(Products);
    private static readonly IReadOnlyDictionary<string, int> ProductIndexById = BuildProductIndexMap(Products);
    private static readonly Encoding Utf8 = Encoding.UTF8;
    private static readonly GroceryStoreJsonContext JsonContext = new(new());
    private const uint OptimizedCartFormatMagicV1 = 0x56434331; // "VCC1"
    private const uint OptimizedCartFormatMagicV2 = 0x56434332; // "VCC2"
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

    public VapeCacheRawGroceryStoreService(
        IRedisCommandExecutor redis,
        string? keyPrefix = null,
        bool optimizedCleanupOnly = false,
        bool useLocalFlashSaleCountCache = false)
    {
        _redis = redis;
        _keyPrefix = NormalizeKeyPrefix(keyPrefix);
        _optimizedCleanupOnly = optimizedCleanupOnly;
        _flashSaleCounts = useLocalFlashSaleCountCache
            ? new ConcurrentDictionary<string, long>(StringComparer.Ordinal)
            : null;
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<Product?> GetProductAsync(string productId)
    {
        Interlocked.Increment(ref _productReadOps);
        var key = Key($"product:{productId}");
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
        Interlocked.Increment(ref _productWriteOps);
        var key = Key($"product:{product.Id}");
        var serialized = Serialize(product);
        await _redis.SetAsync(key, serialized, ttl, CancellationToken.None);
    }

    /// <summary>
    /// Adds value.
    /// </summary>
    public async ValueTask AddToCartAsync(string userId, CartItem item)
    {
        Interlocked.Increment(ref _cartItemWriteOps);
        var key = Key($"cart:{userId}");
        var serialized = Serialize(item);
        await _redis.RPushAsync(key, serialized, CancellationToken.None);
    }

    /// <summary>
    /// Adds value.
    /// </summary>
    public async ValueTask AddToCartBatchAsync(string userId, IReadOnlyList<CartItem> items)
    {
        if (items.Count == 0)
            return;
        Interlocked.Add(ref _cartItemWriteOps, items.Count);

        var optimizedCartKey = GetOptimizedCartKey(userId);
        var cartPayload = SerializeCartItems(items);
        var countPayload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(countPayload, items.Count);

        var setCart = _redis.SetAsync(optimizedCartKey, cartPayload, TimeSpan.FromMinutes(15), CancellationToken.None);
        var setCount = _redis.SetAsync(GetOptimizedCartCountKey(userId), countPayload, TimeSpan.FromMinutes(15), CancellationToken.None);

        _ = await AwaitValueTask(setCart).ConfigureAwait(false);
        _ = await AwaitValueTask(setCount).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<CartItem[]> GetCartAsync(string userId)
    {
        Interlocked.Increment(ref _cartReadOps);
        using var optimized = await _redis.GetLeaseAsync(GetOptimizedCartKey(userId), CancellationToken.None);
        if (!optimized.IsNull)
        {
            return DeserializeCartItemsOptimized(optimized.Span);
        }

        var values = await _redis.LRangeAsync(Key($"cart:{userId}"), 0, -1, CancellationToken.None);
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

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<long> GetCartCountAsync(string userId)
    {
        Interlocked.Increment(ref _cartCountReadOps);
        // Fast path for optimized carts: tiny fixed-size count key.
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

        // Backward compatibility with optimized payload-only carts.
        using var optimizedCart = await _redis.GetLeaseAsync(GetOptimizedCartKey(userId), CancellationToken.None);
        if (!optimizedCart.IsNull && TryReadOptimizedCartCount(optimizedCart.Span, out var optimizedCartCount))
            return optimizedCartCount;

        return await _redis.LLenAsync(Key($"cart:{userId}"), CancellationToken.None);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask ClearCartAsync(string userId)
    {
        Interlocked.Increment(ref _cartClearWriteOps);
        var optimizedKey = GetOptimizedCartKey(userId);
        var countKey = GetOptimizedCartCountKey(userId);
        var legacyListKey = Key($"cart:{userId}");

        if (_optimizedCleanupOnly)
        {
            var unlinkOptimized = _redis.UnlinkAsync(optimizedKey, CancellationToken.None);
            var unlinkCount = _redis.UnlinkAsync(countKey, CancellationToken.None);

            var optimizedRemoved = await AwaitValueTask(unlinkOptimized).ConfigureAwait(false);
            _ = await AwaitValueTask(unlinkCount).ConfigureAwait(false);

            if (optimizedRemoved > 0)
                return;

            _ = await _redis.UnlinkAsync(legacyListKey, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var cleanupOptimized = _redis.UnlinkAsync(optimizedKey, CancellationToken.None);
        var cleanupCount = _redis.UnlinkAsync(countKey, CancellationToken.None);
        var cleanupLegacyList = _redis.UnlinkAsync(legacyListKey, CancellationToken.None);

        _ = await AwaitValueTask(cleanupOptimized).ConfigureAwait(false);
        _ = await AwaitValueTask(cleanupCount).ConfigureAwait(false);
        _ = await AwaitValueTask(cleanupLegacyList).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask JoinFlashSaleAsync(string saleId, string userId)
    {
        Interlocked.Increment(ref _flashSaleJoinWriteOps);
        var added = await ExecuteWithRentedUtf8Async(userId, payload =>
            _redis.SAddAsync(Key($"sale:{saleId}:participants"), payload, CancellationToken.None)).ConfigureAwait(false);

        if (added > 0 && _flashSaleCounts is not null)
            _flashSaleCounts.AddOrUpdate(saleId, 1L, static (_, current) => current + 1L);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> IsInFlashSaleAsync(string saleId, string userId)
    {
        Interlocked.Increment(ref _flashSaleMembershipReadOps);
        return await ExecuteWithRentedUtf8Async(userId, payload =>
            _redis.SIsMemberAsync(Key($"sale:{saleId}:participants"), payload, CancellationToken.None)).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<long> GetFlashSaleParticipantCountAsync(string saleId)
    {
        Interlocked.Increment(ref _flashSaleParticipantCountReadOps);
        if (_flashSaleCounts is not null && _flashSaleCounts.TryGetValue(saleId, out var cachedCount))
            return cachedCount;

        var count = await _redis.SCardAsync(Key($"sale:{saleId}:participants"), CancellationToken.None).ConfigureAwait(false);
        if (_flashSaleCounts is not null)
            _flashSaleCounts[saleId] = count;

        return count;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask SaveSessionAsync(string sessionId, UserSession session)
    {
        Interlocked.Increment(ref _sessionWriteOps);
        var serialized = SessionBinaryCodec.Serialize(session);
        await _redis.SetAsync(Key($"session:{sessionId}"), serialized, TimeSpan.FromHours(1), CancellationToken.None);
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<UserSession?> GetSessionAsync(string sessionId)
    {
        Interlocked.Increment(ref _sessionReadOps);
        using var lease = await _redis.GetLeaseAsync(Key($"session:{sessionId}"), CancellationToken.None);
        if (lease.IsNull)
            return null;

        if (SessionBinaryCodec.TryDeserialize(lease.Span, out var session))
            return session;

        // Backward compatibility with legacy JSON session payloads.
        return Deserialize<UserSession>(lease.Span);
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

    private static bool TryReadOptimizedCartCount(ReadOnlySpan<byte> payload, out int count)
    {
        count = 0;
        if (payload.Length < 8)
            return false;

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, 4));
        if (magic != OptimizedCartFormatMagicV1 && magic != OptimizedCartFormatMagicV2)
            return false;

        count = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4));
        return count >= 0;
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

    private static async ValueTask<T> AwaitValueTask<T>(ValueTask<T> task)
    {
        if (task.IsCompletedSuccessfully)
            return task.Result;

        return await task.ConfigureAwait(false);
    }

    private string GetOptimizedCartKey(string userId) => Key($"cart:optimized:{userId}");

    private string GetOptimizedCartCountKey(string userId) => Key($"cart:optimized:{userId}:count");

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
