using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Modules;
using VapeCache.Features.Search;

namespace VapeCache.Console.GroceryStore;

internal interface ISuperCenterStoreProvider
{
    ValueTask<Product?> GetProductAsync(string productId, CancellationToken ct);
    ValueTask CacheProductAsync(Product product, TimeSpan ttl, CancellationToken ct);
    ValueTask<CartItem[]> GetCartAsync(string shopperId, CancellationToken ct);
    ValueTask SetCartAsync(string shopperId, CartItem[] items, TimeSpan ttl, CancellationToken ct);
    ValueTask<long> GetCartCountAsync(string shopperId, CancellationToken ct);
    ValueTask RemoveCartAsync(string shopperId, CancellationToken ct);
    ValueTask JoinFlashSaleAsync(string saleId, string shopperId, CancellationToken ct);
    ValueTask<bool> IsInFlashSaleAsync(string saleId, string shopperId, CancellationToken ct);
    ValueTask<long> GetFlashSaleParticipantCountAsync(string saleId, CancellationToken ct);
    ValueTask SaveSessionAsync(string sessionId, UserSession session, TimeSpan ttl, CancellationToken ct);
    ValueTask<UserSession?> GetSessionAsync(string sessionId, CancellationToken ct);
    ValueTask TrackRecentlyViewedAsync(string shopperId, IReadOnlyList<string> productIds, DateTime viewedAtUtc, TimeSpan ttl, CancellationToken ct);
    ValueTask<string[]> GetRecentlyViewedAsync(string shopperId, int take, CancellationToken ct);
    ValueTask RecordCheckoutEventAsync(GroceryCheckoutEvent checkoutEvent, TimeSpan ttl, CancellationToken ct);
    ValueTask<ReceiptSearchLookup> SearchReceiptAsync(ReceiptExitCheckRequest request, CancellationToken ct);
    ValueTask<bool> RemoveKeyAsync(string key, CancellationToken ct);
    ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct);
    ValueTask<SuperCenterCommandCoverageSnapshot> ExecuteCommandCoverageAsync(
        SuperCenterCommandCoverageContext context,
        CancellationToken ct);
}

internal sealed class VapeCacheSuperCenterProvider : ISuperCenterStoreProvider
{
    private static readonly GroceryStoreJsonContext JsonContext = new(new());
    private static readonly Encoding Utf8 = Encoding.UTF8;
    private static readonly TimeSpan CommandCoverageTtl = TimeSpan.FromMinutes(5);
    private static readonly byte[] CleanupPayload = "cleanup"u8.ToArray();
    private static readonly byte[] CoverageJsonPrefix = "{\"u\":\""u8.ToArray();
    private static readonly byte[] CoverageJsonSalePrefix = "\",\"s\":\""u8.ToArray();
    private static readonly byte[] CoverageJsonProductPrefix = "\",\"p\":\""u8.ToArray();
    private static readonly byte[] CoverageJsonCountPrefix = "\",\"n\":"u8.ToArray();
    private static readonly byte[] CoverageJsonSuffix = "}"u8.ToArray();
    private static readonly byte[] StreamProbePayload = "1"u8.ToArray();
    private static readonly string[] CoverageHashFields = ["shopper", "sale", "product"];
    private static readonly ConcurrentDictionary<string, string> SaleParticipantKeyCache = new(StringComparer.Ordinal);
    [ThreadStatic] private static string[]? _tlsTwoStringArray;
    [ThreadStatic] private static string[]? _tlsThirteenStringArray;
    [ThreadStatic] private static (string, ReadOnlyMemory<byte>)[]? _tlsTwoFieldValueArray;
    [ThreadStatic] private static (string, ReadOnlyMemory<byte>)[]? _tlsThreeFieldValueArray;
    private readonly IRedisCommandExecutor _redis;
    private readonly IRedisHashSearchDocumentStore<ReceiptSearchDocument>? _receiptSearchDocuments;
    private readonly string _keyPrefix;
    private readonly string _receiptSearchDocumentIdPrefix;
    private readonly SemaphoreSlim _coverageSetupGate = new(1, 1);
    private SuperCenterModuleCapabilities _coverageCapabilities;
    private int _coverageSetupComplete;

    public VapeCacheSuperCenterProvider(
        IRedisCommandExecutor redis,
        string? keyPrefix = null,
        IRedisHashSearchDocumentStore<ReceiptSearchDocument>? receiptSearchDocuments = null,
        string? receiptSearchDocumentIdPrefix = null)
    {
        _redis = redis;
        _receiptSearchDocuments = receiptSearchDocuments;
        _keyPrefix = NormalizeKeyPrefix(keyPrefix);
        _receiptSearchDocumentIdPrefix = NormalizeKeyPrefix(receiptSearchDocumentIdPrefix ?? keyPrefix);
    }

    public async ValueTask<Product?> GetProductAsync(string productId, CancellationToken ct)
    {
        using var lease = await _redis.HGetLeaseAsync(Key(SuperCenterKeySpace.Product(productId)), "payload", ct).ConfigureAwait(false);
        if (lease.IsNull)
            return null;

        return JsonSerializer.Deserialize(lease.Span, JsonContext.Product);
    }

    public async ValueTask CacheProductAsync(Product product, TimeSpan ttl, CancellationToken ct)
    {
        var productKey = Key(SuperCenterKeySpace.Product(product.Id));
        var payload = JsonSerializer.SerializeToUtf8Bytes(product, JsonContext.Product);

        await SetTaggedHashAsync(
            productKey,
            [
                ("payload", payload),
                ("category", Utf8.GetBytes(product.Category)),
                ("department", Utf8.GetBytes(product.Department)),
                ("aisle", Utf8.GetBytes(product.Aisle)),
                ("price", Utf8.GetBytes(product.Price.ToString(CultureInfo.InvariantCulture))),
                ("temperatureZone", Utf8.GetBytes(product.TemperatureZone))
            ],
            ttl,
            [
                $"product:{product.Id}",
                $"category:{product.Category.ToLowerInvariant()}",
                CacheTagConventions.ToZoneTag("catalog")
            ],
            ct).ConfigureAwait(false);
    }

    public async ValueTask<CartItem[]> GetCartAsync(string shopperId, CancellationToken ct)
    {
        var values = await _redis.LRangeAsync(Key(SuperCenterKeySpace.Cart(shopperId)), 0, -1, ct).ConfigureAwait(false);
        if (values.Length == 0)
            return Array.Empty<CartItem>();

        var items = new CartItem[values.Length];
        for (var i = 0; i < values.Length; i++)
            items[i] = JsonSerializer.Deserialize(values[i], JsonContext.CartItem)!;

        return items;
    }

    public async ValueTask SetCartAsync(string shopperId, CartItem[] items, TimeSpan ttl, CancellationToken ct)
    {
        var cartKey = Key(SuperCenterKeySpace.Cart(shopperId));
        _ = await _redis.UnlinkAsync(cartKey, ct).ConfigureAwait(false);
        if (items.Length > 0)
        {
            var cartPayloads = ArrayPool<ReadOnlyMemory<byte>>.Shared.Rent(items.Length);
            try
            {
                for (var i = 0; i < items.Length; i++)
                    cartPayloads[i] = JsonSerializer.SerializeToUtf8Bytes(items[i], JsonContext.CartItem);

                _ = await _redis.RPushManyAsync(cartKey, cartPayloads, items.Length, ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(cartPayloads, clearArray: true);
            }
        }

        _ = await _redis.ExpireAsync(cartKey, ttl, ct).ConfigureAwait(false);
        await TrackTagsAsync(cartKey, BuildCartTags(shopperId), ct).ConfigureAwait(false);
    }

    public ValueTask<long> GetCartCountAsync(string shopperId, CancellationToken ct)
        => _redis.LLenAsync(Key(SuperCenterKeySpace.Cart(shopperId)), ct);

    public async ValueTask RemoveCartAsync(string shopperId, CancellationToken ct)
        => _ = await _redis.UnlinkAsync(Key(SuperCenterKeySpace.Cart(shopperId)), ct).ConfigureAwait(false);

    public async ValueTask JoinFlashSaleAsync(string saleId, string shopperId, CancellationToken ct)
    {
        using var shopperBytes = RentUtf8(shopperId);
        _ = await _redis.SAddAsync(GetSaleParticipantKey(saleId), shopperBytes.Memory, ct).ConfigureAwait(false);
    }

    public async ValueTask<bool> IsInFlashSaleAsync(string saleId, string shopperId, CancellationToken ct)
    {
        using var shopperBytes = RentUtf8(shopperId);
        return await _redis.SIsMemberAsync(GetSaleParticipantKey(saleId), shopperBytes.Memory, ct).ConfigureAwait(false);
    }

    public ValueTask<long> GetFlashSaleParticipantCountAsync(string saleId, CancellationToken ct)
        => _redis.SCardAsync(GetSaleParticipantKey(saleId), ct);

    public async ValueTask SaveSessionAsync(string sessionId, UserSession session, TimeSpan ttl, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(session, JsonContext.UserSession);
        var sessionKey = Key(SuperCenterKeySpace.Session(sessionId));
        using var shopperId = RentUtf8(session.UserId);
        using var storeId = RentUtf8(session.StoreId);
        using var loyaltyTier = RentUtf8(session.LoyaltyTier);
        using var fulfillmentMethod = RentUtf8(session.FulfillmentMethod);
        using var lastActivityTicks = RentUtf8(session.LastActivityAt.Ticks.ToString(CultureInfo.InvariantCulture));

        var entries = new (string Field, ReadOnlyMemory<byte> Value)[6];
        try
        {
            entries[0] = ("payload", payload);
            entries[1] = ("shopperId", shopperId.Memory);
            entries[2] = ("storeId", storeId.Memory);
            entries[3] = ("loyaltyTier", loyaltyTier.Memory);
            entries[4] = ("fulfillmentMethod", fulfillmentMethod.Memory);
            entries[5] = ("lastActivityTicks", lastActivityTicks.Memory);

            _ = await _redis.HSetManyAsync(sessionKey, entries, ct).ConfigureAwait(false);
            _ = await _redis.ExpireAsync(sessionKey, ttl, ct).ConfigureAwait(false);
            await TrackTagsAsync(
                sessionKey,
                [
                    SuperCenterKeySpace.ShopperTag(session.UserId),
                    SuperCenterKeySpace.ShopperTag(session.UserId, "session")
                ],
                ct).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(entries, 0, entries.Length);
        }
    }

    public async ValueTask<UserSession?> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        using var lease = await _redis.HGetLeaseAsync(Key(SuperCenterKeySpace.Session(sessionId)), "payload", ct).ConfigureAwait(false);
        if (lease.IsNull)
            return null;

        return JsonSerializer.Deserialize(lease.Span, JsonContext.UserSession);
    }

    public async ValueTask TrackRecentlyViewedAsync(
        string shopperId,
        IReadOnlyList<string> productIds,
        DateTime viewedAtUtc,
        TimeSpan ttl,
        CancellationToken ct)
    {
        if (productIds.Count == 0)
            return;

        var key = Key(SuperCenterKeySpace.RecentlyViewed(shopperId));
        var baseScore = new DateTimeOffset(viewedAtUtc).ToUnixTimeMilliseconds();
        for (var i = 0; i < productIds.Count; i++)
        {
            using var productBytes = RentUtf8(productIds[i]);
            _ = await _redis.ZAddAsync(key, baseScore + i, productBytes.Memory, ct).ConfigureAwait(false);
        }

        _ = await _redis.ExpireAsync(key, ttl, ct).ConfigureAwait(false);
        await TrackTagsAsync(
            key,
            [
                SuperCenterKeySpace.ShopperTag(shopperId),
                SuperCenterKeySpace.ShopperTag(shopperId, "recent")
            ],
            ct).ConfigureAwait(false);
    }

    public async ValueTask<string[]> GetRecentlyViewedAsync(string shopperId, int take, CancellationToken ct)
    {
        if (take <= 0)
            return Array.Empty<string>();

        var values = await _redis
            .ZRangeWithScoresAsync(Key(SuperCenterKeySpace.RecentlyViewed(shopperId)), 0, take - 1, descending: true, ct)
            .ConfigureAwait(false);
        if (values.Length == 0)
            return Array.Empty<string>();

        var products = new string[values.Length];
        for (var i = 0; i < values.Length; i++)
            products[i] = Utf8.GetString(values[i].Member);

        return products;
    }

    public async ValueTask RecordCheckoutEventAsync(GroceryCheckoutEvent checkoutEvent, TimeSpan ttl, CancellationToken ct)
    {
        using var orderId = RentUtf8(checkoutEvent.OrderId);
        using var shopperId = RentUtf8(checkoutEvent.ShopperId);
        using var sessionId = RentUtf8(checkoutEvent.SessionId);
        using var saleId = RentUtf8(checkoutEvent.SaleId);
        using var storeId = RentUtf8(checkoutEvent.StoreId);
        using var fulfillmentMethod = RentUtf8(checkoutEvent.FulfillmentMethod);
        using var itemCount = RentUtf8(checkoutEvent.ItemCount.ToString(CultureInfo.InvariantCulture));
        using var subtotal = RentUtf8(checkoutEvent.Subtotal.ToString(CultureInfo.InvariantCulture));
        using var checkedOutAt = RentUtf8(checkoutEvent.CheckedOutAtUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        _ = await _redis.XAddIdempotentAckAsync(
            Key(SuperCenterKeySpace.CheckoutStream(checkoutEvent.ShopperId)),
            producerId: "grocery-store",
            idempotentId: checkoutEvent.OrderId,
            useAutoIdempotentId: false,
            entryId: "*",
            fields:
            [
                ("orderId", orderId.Memory),
                ("shopperId", shopperId.Memory),
                ("sessionId", sessionId.Memory),
                ("saleId", saleId.Memory),
                ("storeId", storeId.Memory),
                ("fulfillmentMethod", fulfillmentMethod.Memory),
                ("itemCount", itemCount.Memory),
                ("subtotal", subtotal.Memory),
                ("checkedOutAtUtc", checkedOutAt.Memory)
            ],
            ct).ConfigureAwait(false);
        _ = await _redis.ExpireAsync(Key(SuperCenterKeySpace.CheckoutStream(checkoutEvent.ShopperId)), ttl, ct).ConfigureAwait(false);
        if (_receiptSearchDocuments is not null)
        {
            _ = await _receiptSearchDocuments.EnsureIndexAsync(ct).ConfigureAwait(false);
            _ = await _receiptSearchDocuments
                .UpsertAsync(
                    SuperCenterReceiptSearch.CreateDocument(checkoutEvent, _receiptSearchDocumentIdPrefix),
                    ttl,
                    ct)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask<ReceiptSearchLookup> SearchReceiptAsync(ReceiptExitCheckRequest request, CancellationToken ct)
    {
        var documentKey = GetReceiptSearchDocumentKey(request.OrderId);
        if (_receiptSearchDocuments is null)
            return new ReceiptSearchLookup(false, 0, documentKey);

        var hitCount = await _receiptSearchDocuments
            .SearchCountAsync(BuildReceiptSearchQuery(request), ct)
            .ConfigureAwait(false);
        return new ReceiptSearchLookup(hitCount > 0, hitCount, documentKey);
    }

    public async ValueTask<bool> RemoveKeyAsync(string key, CancellationToken ct)
        => await _redis.DeleteAsync(ResolveDeleteKey(key), ct).ConfigureAwait(false);

    public async ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct)
    {
        var tagKey = Key($"tag:{tag}:keys");
        var members = await _redis.SMembersAsync(tagKey, ct).ConfigureAwait(false);
        if (members.Length == 0)
        {
            _ = await _redis.DeleteAsync(tagKey, ct).ConfigureAwait(false);
            return 0;
        }

        var deleted = await _redis.UnlinkManyAsync(members, ct).ConfigureAwait(false);
        _ = await _redis.DeleteAsync(tagKey, ct).ConfigureAwait(false);
        return deleted;
    }

    public async ValueTask<SuperCenterCommandCoverageSnapshot> ExecuteCommandCoverageAsync(
        SuperCenterCommandCoverageContext context,
        CancellationToken ct)
    {
        var capabilities = await EnsureCoverageSetupAsync(ct).ConfigureAwait(false);
        long readOps = 0;
        long writeOps = 0;
        long adminOps = 0;
        long optionalSkips = 0;

        _ = await _redis.PingAsync(ct).ConfigureAwait(false);
        adminOps++;

        _ = await _redis.ModuleListAsync(ct).ConfigureAwait(false);
        adminOps++;

        var auditBase = Key($"audit:{context.ShopperId}");

        var profileKey = string.Concat(auditBase, ":profile");
        var windowKey = string.Concat(auditBase, ":window");
        var batchKeyA = string.Concat(auditBase, ":batch:a");
        var batchKeyB = string.Concat(auditBase, ":batch:b");
        var deleteKey = string.Concat(auditBase, ":delete");
        var hashKey = string.Concat(auditBase, ":hash");
        var listKey = string.Concat(auditBase, ":list");
        var setKey = string.Concat(auditBase, ":set");
        var zsetKey = string.Concat(auditBase, ":zset");
        var docKey = string.Concat(Key("audit:search:doc:"), context.ShopperId);
        var jsonKey = string.Concat(auditBase, ":json");
        var bloomKey = string.Concat(auditBase, ":bloom");
        var seriesKey = string.Concat(auditBase, ":ts");
        var streamKey = string.Concat(auditBase, ":stream");

        var firstItem = context.Items.Length == 0
            ? new CartItem("fallback-item", "Fallback Item", 1m, 1, context.TimestampUtc)
            : context.Items[0];
        var secondItem = context.Items.Length > 1 ? context.Items[1] : firstItem;
        using var shopperBytes = RentUtf8(context.ShopperId);
        using var saleBytes = RentUtf8(context.SaleId);
        using var sessionBytes = RentUtf8(context.SessionId);
        using var firstProductBytes = RentUtf8(firstItem.ProductId);
        using var secondProductBytes = RentUtf8(secondItem.ProductId);
        using var secondProductNameBytes = RentUtf8(secondItem.ProductName);
        using var profilePayload = RentJoinedUtf8(context.ShopperId, context.SaleId);
        using var windowPayload = RentJoinedUtf8(firstItem.ProductId, secondItem.ProductId);
        using var batchPayloadsLease = RentTwoFieldValueArray();
        var batchPayloads = batchPayloadsLease.Array;
        batchPayloads[0] = (batchKeyA, firstProductBytes.Memory);
        batchPayloads[1] = (batchKeyB, secondProductNameBytes.Memory);

        _ = await _redis.SetAsync(profileKey, profilePayload.Memory, CommandCoverageTtl, ct).ConfigureAwait(false);
        writeOps++;
        _ = await _redis.GetDiscardAsync(profileKey, ct).ConfigureAwait(false);
        readOps++;
        _ = await _redis.GetExDiscardAsync(profileKey, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        readOps++;
        _ = await _redis.TtlSecondsAsync(profileKey, ct).ConfigureAwait(false);
        readOps++;
        _ = await _redis.PTtlMillisecondsAsync(profileKey, ct).ConfigureAwait(false);
        readOps++;
        _ = await _redis.ExpireAsync(profileKey, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
        writeOps++;

        _ = await _redis.SetAsync(windowKey, windowPayload.Memory, CommandCoverageTtl, ct).ConfigureAwait(false);
        writeOps++;
        _ = await _redis.GetRangeDiscardAsync(windowKey, 0, 0, ct).ConfigureAwait(false);
        readOps++;

        _ = await _redis.MSetAsync(batchPayloads, ct).ConfigureAwait(false);
        writeOps++;
        using var batchKeysLease = RentTwoStringArray();
        var batchKeys = batchKeysLease.Array;
        batchKeys[0] = batchKeyA;
        batchKeys[1] = batchKeyB;
        _ = await _redis.MGetCountAsync(batchKeys, ct).ConfigureAwait(false);
        readOps++;

        _ = await _redis.SetAsync(deleteKey, CleanupPayload, CommandCoverageTtl, ct).ConfigureAwait(false);
        writeOps++;
        _ = await _redis.DeleteAsync(deleteKey, ct).ConfigureAwait(false);
        writeOps++;

        using var hashEntriesLease = RentThreeFieldValueArray();
        var hashEntries = hashEntriesLease.Array;
        hashEntries[0] = ("shopper", shopperBytes.Memory);
        hashEntries[1] = ("sale", saleBytes.Memory);
        hashEntries[2] = ("product", firstProductBytes.Memory);
        _ = await _redis.HSetManyAsync(hashKey, hashEntries, ct).ConfigureAwait(false);
        writeOps++;
        _ = await _redis.HGetDiscardAsync(hashKey, "shopper", ct).ConfigureAwait(false);
        readOps++;
        _ = await _redis.HMGetCountAsync(hashKey, CoverageHashFields, ct).ConfigureAwait(false);
        readOps++;

        _ = await _redis.LPushAsync(listKey, firstProductBytes.Memory, ct).ConfigureAwait(false);
        writeOps++;
        _ = await _redis.RPushAsync(listKey, secondProductBytes.Memory, ct).ConfigureAwait(false);
        writeOps++;
        var listValues = ArrayPool<ReadOnlyMemory<byte>>.Shared.Rent(2);
        listValues[0] = sessionBytes.Memory;
        listValues[1] = saleBytes.Memory;
        try
        {
            _ = await _redis.RPushManyAsync(listKey, listValues, 2, ct).ConfigureAwait(false);
            writeOps++;
        }
        finally
        {
            ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(listValues, clearArray: true);
        }
        _ = await _redis.LIndexDiscardAsync(listKey, 0, ct).ConfigureAwait(false);
        readOps++;
        _ = await _redis.LRangeCountAsync(listKey, 0, -1, ct).ConfigureAwait(false);
        readOps++;
        _ = await _redis.LLenAsync(listKey, ct).ConfigureAwait(false);
        readOps++;
        _ = await _redis.LPopDiscardAsync(listKey, ct).ConfigureAwait(false);
        readOps++;
        _ = await _redis.RPopDiscardAsync(listKey, ct).ConfigureAwait(false);
        readOps++;

        _ = await _redis.SAddAsync(setKey, shopperBytes.Memory, ct).ConfigureAwait(false);
        writeOps++;
        _ = await _redis.SAddAsync(setKey, firstProductBytes.Memory, ct).ConfigureAwait(false);
        writeOps++;
        _ = await _redis.SIsMemberAsync(setKey, shopperBytes.Memory, ct).ConfigureAwait(false);
        readOps++;
        _ = await _redis.SMembersCountAsync(setKey, ct).ConfigureAwait(false);
        readOps++;
        _ = await _redis.SCardAsync(setKey, ct).ConfigureAwait(false);
        readOps++;
        _ = await _redis.SRemAsync(setKey, firstProductBytes.Memory, ct).ConfigureAwait(false);
        writeOps++;

        _ = await _redis.ZAddAsync(zsetKey, firstItem.Quantity, firstProductBytes.Memory, ct).ConfigureAwait(false);
        writeOps++;
        _ = await _redis.ZAddAsync(zsetKey, secondItem.Quantity + 0.5d, secondProductBytes.Memory, ct).ConfigureAwait(false);
        writeOps++;
        _ = await _redis.ZScoreAsync(zsetKey, firstProductBytes.Memory, ct).ConfigureAwait(false);
        readOps++;
        _ = await _redis.ZRankAsync(zsetKey, secondProductBytes.Memory, descending: false, ct).ConfigureAwait(false);
        readOps++;
        _ = await _redis.ZIncrByAsync(zsetKey, 1d, firstProductBytes.Memory, ct).ConfigureAwait(false);
        writeOps++;
        _ = await _redis.ZRangeWithScoresCountAsync(zsetKey, 0, -1, descending: false, ct).ConfigureAwait(false);
        readOps++;
        _ = await _redis.ZRangeByScoreWithScoresCountAsync(zsetKey, double.NegativeInfinity, double.PositiveInfinity, descending: false, offset: 0, count: 10, ct).ConfigureAwait(false);
        readOps++;
        _ = await _redis.ZCardAsync(zsetKey, ct).ConfigureAwait(false);
        readOps++;
        _ = await _redis.ZRemAsync(zsetKey, secondProductBytes.Memory, ct).ConfigureAwait(false);
        writeOps++;

        if (capabilities.SupportsJson)
        {
            using var jsonPayload = RentCoverageJsonPayload(context.ShopperId, context.SaleId, firstItem.ProductId, context.Items.Length);
            _ = await _redis.JsonSetAsync(jsonKey, ".", jsonPayload.Memory, ct).ConfigureAwait(false);
            writeOps++;
            _ = await _redis.JsonGetDiscardAsync(jsonKey, ".u", ct).ConfigureAwait(false);
            readOps++;
            _ = await _redis.JsonDelAsync(jsonKey, ".", ct).ConfigureAwait(false);
            writeOps++;
        }
        else
        {
            optionalSkips += 3;
        }

        if (capabilities.SupportsSearch)
        {
            using var searchEntriesLease = RentThreeFieldValueArray();
            var searchEntries = searchEntriesLease.Array;
            searchEntries[0] = ("shopper", shopperBytes.Memory);
            searchEntries[1] = ("product", firstProductBytes.Memory);
            searchEntries[2] = ("sale", saleBytes.Memory);
            _ = await _redis.HSetManyAsync(docKey, searchEntries, ct).ConfigureAwait(false);
            writeOps++;
            _ = await _redis.FtSearchCountAsync(CoverageIndexName(), context.ShopperId, 0, 0, ct).ConfigureAwait(false);
            readOps++;
        }
        else
        {
            optionalSkips += 4;
        }

        if (capabilities.SupportsBloom)
        {
            _ = await _redis.BfAddAsync(bloomKey, sessionBytes.Memory, ct).ConfigureAwait(false);
            writeOps++;
            _ = await _redis.BfExistsAsync(bloomKey, sessionBytes.Memory, ct).ConfigureAwait(false);
            readOps++;
        }
        else
        {
            optionalSkips += 2;
        }

        if (capabilities.SupportsTimeSeries)
        {
            _ = await _redis.TsCreateAsync(seriesKey, ct).ConfigureAwait(false);
            writeOps++;
            var ts = new DateTimeOffset(context.TimestampUtc).ToUnixTimeMilliseconds();
            _ = await _redis.TsAddAsync(seriesKey, ts, context.Items.Length, ct).ConfigureAwait(false);
            writeOps++;
            _ = await _redis.TsRangeCountAsync(seriesKey, ts, ts, ct).ConfigureAwait(false);
            readOps++;
        }
        else
        {
            optionalSkips += 3;
        }

        if (capabilities.SupportsIdempotentStreams)
        {
            using var streamFieldsLease = RentTwoFieldValueArray();
            var streamFields = streamFieldsLease.Array;
            streamFields[0] = ("shopper", shopperBytes.Memory);
            streamFields[1] = ("sale", saleBytes.Memory);
            _ = await _redis.XAddIdempotentAckAsync(
                streamKey,
                producerId: "grocery-bench",
                idempotentId: $"evt:{context.ShopperId}",
                useAutoIdempotentId: false,
                entryId: "*",
                fields: streamFields,
                ct).ConfigureAwait(false);
            writeOps++;
            _ = await _redis.XCfgSetIdempotenceAsync(streamKey, durationSeconds: 600, maxSize: 128, ct).ConfigureAwait(false);
            adminOps++;
        }
        else
        {
            optionalSkips += 2;
        }

        using var cleanupKeysLease = RentThirteenStringArray();
        var cleanupKeys = cleanupKeysLease.Array;
        cleanupKeys[0] = hashKey;
        cleanupKeys[1] = listKey;
        cleanupKeys[2] = setKey;
        cleanupKeys[3] = zsetKey;
        cleanupKeys[4] = profileKey;
        cleanupKeys[5] = windowKey;
        cleanupKeys[6] = batchKeyA;
        cleanupKeys[7] = batchKeyB;
        cleanupKeys[8] = docKey;
        cleanupKeys[9] = jsonKey;
        cleanupKeys[10] = bloomKey;
        cleanupKeys[11] = seriesKey;
        cleanupKeys[12] = streamKey;
        _ = await _redis.UnlinkManyAsync(cleanupKeys, ct).ConfigureAwait(false);
        writeOps++;

        return new SuperCenterCommandCoverageSnapshot(readOps, writeOps, adminOps, optionalSkips);
    }

    private async ValueTask<SuperCenterModuleCapabilities> EnsureCoverageSetupAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _coverageSetupComplete) != 0)
            return _coverageCapabilities;

        await _coverageSetupGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _coverageSetupComplete) == 0)
            {
                _coverageCapabilities = await DiscoverCoverageCapabilitiesAsync(ct).ConfigureAwait(false);
                if (_coverageCapabilities.SupportsSearch)
                    _ = await _redis.FtCreateAsync(CoverageIndexName(), Key("audit:search:doc:"), ["shopper", "product", "sale"], ct).ConfigureAwait(false);

                Volatile.Write(ref _coverageSetupComplete, 1);
            }

            return _coverageCapabilities;
        }
        finally
        {
            _coverageSetupGate.Release();
        }
    }

    private async ValueTask<SuperCenterModuleCapabilities> DiscoverCoverageCapabilitiesAsync(CancellationToken ct)
    {
        var modules = await _redis.ModuleListAsync(ct).ConfigureAwait(false);
        var supportsJson = false;
        var supportsSearch = false;
        var supportsBloom = false;
        var supportsTimeSeries = false;

        for (var i = 0; i < modules.Length; i++)
        {
            var module = modules[i];
            if (module.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                module.Contains("rejson", StringComparison.OrdinalIgnoreCase))
            {
                supportsJson = true;
            }

            if (module.Contains("search", StringComparison.OrdinalIgnoreCase) ||
                module.Contains("ft", StringComparison.OrdinalIgnoreCase))
            {
                supportsSearch = true;
            }

            if (module.Contains("bloom", StringComparison.OrdinalIgnoreCase) ||
                module.Contains("bf", StringComparison.OrdinalIgnoreCase))
            {
                supportsBloom = true;
            }

            if (module.Contains("timeseries", StringComparison.OrdinalIgnoreCase) ||
                module.Contains("ts", StringComparison.OrdinalIgnoreCase))
            {
                supportsTimeSeries = true;
            }
        }

        var supportsIdempotentStreams = await SupportsIdempotentStreamsAsync(ct).ConfigureAwait(false);
        return new SuperCenterModuleCapabilities(
            supportsJson,
            supportsSearch,
            supportsBloom,
            supportsTimeSeries,
            supportsIdempotentStreams);
    }

    private string CoverageIndexName()
        => Key("audit:search:index");

    private async ValueTask<bool> SupportsIdempotentStreamsAsync(CancellationToken ct)
    {
        var probeKey = Key("audit:stream:probe");
        try
        {
            _ = await _redis.XAddIdempotentAsync(
                probeKey,
                producerId: "probe",
                idempotentId: "probe-1",
                useAutoIdempotentId: false,
                entryId: "*",
                fields:
                [
                    ("probe", StreamProbePayload)
                ],
                ct).ConfigureAwait(false);
            _ = await _redis.XCfgSetIdempotenceAsync(probeKey, durationSeconds: 60, maxSize: 4, ct).ConfigureAwait(false);
            _ = await _redis.UnlinkAsync(probeKey, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (IsUnsupportedCommand(ex))
        {
            return false;
        }
    }

    private static bool IsUnsupportedCommand(Exception ex)
        => ex.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase) ||
           ex.Message.Contains("unknown subcommand", StringComparison.OrdinalIgnoreCase) ||
           ex.Message.Contains("invalid stream id", StringComparison.OrdinalIgnoreCase) ||
           ex.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase);

    private static string[] BuildCartTags(string shopperId)
        =>
        [
            SuperCenterKeySpace.ShopperTag(shopperId),
            SuperCenterKeySpace.ShopperTag(shopperId, "cart")
        ];

    private async ValueTask SetTaggedHashAsync(
        string key,
        (string Field, byte[] Value)[] fields,
        TimeSpan ttl,
        IReadOnlyCollection<string> tags,
        CancellationToken ct)
    {
        var entries = new (string Field, ReadOnlyMemory<byte> Value)[fields.Length];
        try
        {
            for (var i = 0; i < fields.Length; i++)
                entries[i] = (fields[i].Field, fields[i].Value);

            _ = await _redis.HSetManyAsync(key, entries, ct).ConfigureAwait(false);
            _ = await _redis.ExpireAsync(key, ttl, ct).ConfigureAwait(false);
            await TrackTagsAsync(key, tags, ct).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(entries, 0, entries.Length);
        }
    }

    private async ValueTask TrackTagsAsync(
        string key,
        IReadOnlyCollection<string> tags,
        CancellationToken ct)
    {
        if (tags.Count == 0)
            return;

        using var keyBytes = RentUtf8(key);
        foreach (var tag in tags)
            _ = await _redis.SAddAsync(Key($"tag:{tag}:keys"), keyBytes.Memory, ct).ConfigureAwait(false);
    }

    private string GetSaleParticipantKey(string saleId)
        => SaleParticipantKeyCache.GetOrAdd(
            saleId,
            static (id, prefix) => prefix.Length == 0
                ? string.Concat("sale:", id, ":participants")
                : string.Concat(prefix, "sale:", id, ":participants"),
            _keyPrefix);

    private static RentedUtf8 RentUtf8(string value)
    {
        var byteCount = Utf8.GetByteCount(value);
        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        var written = Utf8.GetBytes(value, rented);
        return new RentedUtf8(rented, written);
    }

    private static RentedUtf8 RentJoinedUtf8(string first, string second)
    {
        var firstByteCount = Utf8.GetByteCount(first);
        var secondByteCount = Utf8.GetByteCount(second);
        var rented = ArrayPool<byte>.Shared.Rent(firstByteCount + 1 + secondByteCount);
        var destination = rented.AsSpan();
        var written = Utf8.GetBytes(first, destination);
        destination[written++] = (byte)'|';
        written += Utf8.GetBytes(second, destination[written..]);
        return new RentedUtf8(rented, written);
    }

    private static RentedUtf8 RentCoverageJsonPayload(string shopperId, string saleId, string productId, int itemCount)
    {
        Span<byte> countBytes = stackalloc byte[11];
        if (!Utf8Formatter.TryFormat(itemCount, countBytes, out var countBytesWritten))
            throw new InvalidOperationException("Unable to format coverage item count.");

        var shopperByteCount = Utf8.GetByteCount(shopperId);
        var saleByteCount = Utf8.GetByteCount(saleId);
        var productByteCount = Utf8.GetByteCount(productId);
        var totalByteCount = CoverageJsonPrefix.Length
            + shopperByteCount
            + CoverageJsonSalePrefix.Length
            + saleByteCount
            + CoverageJsonProductPrefix.Length
            + productByteCount
            + CoverageJsonCountPrefix.Length
            + countBytesWritten
            + CoverageJsonSuffix.Length;

        var rented = ArrayPool<byte>.Shared.Rent(totalByteCount);
        var destination = rented.AsSpan(0, totalByteCount);
        var written = 0;
        CoverageJsonPrefix.CopyTo(destination[written..]);
        written += CoverageJsonPrefix.Length;
        written += Utf8.GetBytes(shopperId, destination[written..]);
        CoverageJsonSalePrefix.CopyTo(destination[written..]);
        written += CoverageJsonSalePrefix.Length;
        written += Utf8.GetBytes(saleId, destination[written..]);
        CoverageJsonProductPrefix.CopyTo(destination[written..]);
        written += CoverageJsonProductPrefix.Length;
        written += Utf8.GetBytes(productId, destination[written..]);
        CoverageJsonCountPrefix.CopyTo(destination[written..]);
        written += CoverageJsonCountPrefix.Length;
        countBytes[..countBytesWritten].CopyTo(destination[written..]);
        written += countBytesWritten;
        CoverageJsonSuffix.CopyTo(destination[written..]);
        written += CoverageJsonSuffix.Length;

        return new RentedUtf8(rented, written);
    }

    private RedisSearchQuery BuildReceiptSearchQuery(ReceiptExitCheckRequest request)
    {
        var builder = new RedisSearchQueryBuilder()
            .Tag("orderId", request.OrderId)
            .Tag("shopperId", request.ShopperId)
            .Tag("storeId", request.StoreId)
            .Tag("receiptStatus", request.ReceiptStatus)
            .NumericRange(
                "checkedOutUnixMilliseconds",
                min: new DateTimeOffset(request.CheckedOutAfterUtc).ToUnixTimeMilliseconds());

        if (!string.IsNullOrWhiteSpace(_receiptSearchDocumentIdPrefix))
            builder.Tag("runScope", _receiptSearchDocumentIdPrefix);

        return builder.Build(offset: 0, count: Math.Clamp(request.Take, 1, 10));
    }

    private string GetReceiptSearchDocumentKey(string orderId)
    {
        var documentId = string.Concat(_receiptSearchDocumentIdPrefix, orderId);
        return _receiptSearchDocuments?.GetDocumentKey(documentId)
            ?? RedisSearchConventions.DocumentKey(SuperCenterReceiptSearch.DefaultDocumentKeyPrefix, documentId);
    }

    private string ResolveDeleteKey(string key)
        => SuperCenterReceiptSearch.IsAbsoluteSearchDocumentKey(key) ? key : Key(key);

    private string Key(string suffix)
        => _keyPrefix.Length == 0 ? suffix : string.Concat(_keyPrefix, suffix);

    private static string NormalizeKeyPrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return string.Empty;

        var trimmed = prefix.Trim();
        return trimmed.EndsWith(':') ? trimmed : string.Concat(trimmed, ":");
    }

    private static StringArrayLease RentTwoStringArray()
        => new(StringArrayCacheKind.Two, _tlsTwoStringArray ??= new string[2]);

    private static StringArrayLease RentThirteenStringArray()
        => new(StringArrayCacheKind.Thirteen, _tlsThirteenStringArray ??= new string[13]);

    private static FieldValueArrayLease RentTwoFieldValueArray()
        => new(FieldValueArrayCacheKind.Two, _tlsTwoFieldValueArray ??= new (string, ReadOnlyMemory<byte>)[2]);

    private static FieldValueArrayLease RentThreeFieldValueArray()
        => new(FieldValueArrayCacheKind.Three, _tlsThreeFieldValueArray ??= new (string, ReadOnlyMemory<byte>)[3]);

    private readonly struct RentedUtf8 : IDisposable
    {
        private readonly byte[] _buffer;
        private readonly int _length;

        public RentedUtf8(byte[] buffer, int length)
        {
            _buffer = buffer;
            _length = length;
        }

        public ReadOnlyMemory<byte> Memory => _buffer.AsMemory(0, _length);

        public void Dispose()
            => ArrayPool<byte>.Shared.Return(_buffer);
    }

    private enum StringArrayCacheKind : byte
    {
        Two,
        Thirteen
    }

    private readonly struct StringArrayLease : IDisposable
    {
        private readonly StringArrayCacheKind _kind;
        private readonly string[] _array;

        public StringArrayLease(StringArrayCacheKind kind, string[] array)
        {
            _kind = kind;
            _array = array;
            switch (kind)
            {
                case StringArrayCacheKind.Two:
                    _tlsTwoStringArray = null;
                    break;
                case StringArrayCacheKind.Thirteen:
                    _tlsThirteenStringArray = null;
                    break;
            }
        }

        public string[] Array => _array;

        public void Dispose()
        {
            System.Array.Clear(_array, 0, _array.Length);
            switch (_kind)
            {
                case StringArrayCacheKind.Two when _array.Length == 2 && _tlsTwoStringArray is null:
                    _tlsTwoStringArray = _array;
                    break;
                case StringArrayCacheKind.Thirteen when _array.Length == 13 && _tlsThirteenStringArray is null:
                    _tlsThirteenStringArray = _array;
                    break;
            }
        }
    }

    private enum FieldValueArrayCacheKind : byte
    {
        Two,
        Three
    }

    private readonly struct FieldValueArrayLease : IDisposable
    {
        private readonly FieldValueArrayCacheKind _kind;
        private readonly (string, ReadOnlyMemory<byte>)[] _array;

        public FieldValueArrayLease(FieldValueArrayCacheKind kind, (string, ReadOnlyMemory<byte>)[] array)
        {
            _kind = kind;
            _array = array;
            switch (kind)
            {
                case FieldValueArrayCacheKind.Two:
                    _tlsTwoFieldValueArray = null;
                    break;
                case FieldValueArrayCacheKind.Three:
                    _tlsThreeFieldValueArray = null;
                    break;
            }
        }

        public (string, ReadOnlyMemory<byte>)[] Array => _array;

        public void Dispose()
        {
            System.Array.Clear(_array, 0, _array.Length);
            switch (_kind)
            {
                case FieldValueArrayCacheKind.Two when _array.Length == 2 && _tlsTwoFieldValueArray is null:
                    _tlsTwoFieldValueArray = _array;
                    break;
                case FieldValueArrayCacheKind.Three when _array.Length == 3 && _tlsThreeFieldValueArray is null:
                    _tlsThreeFieldValueArray = _array;
                    break;
            }
        }
    }
}

internal sealed class StackExchangeSuperCenterProvider : ISuperCenterStoreProvider
{
    private static readonly GroceryStoreJsonContext JsonContext = new(new());
    private static readonly Encoding Utf8 = Encoding.UTF8;
    private static readonly TimeSpan CommandCoverageTtl = TimeSpan.FromMinutes(5);
    private readonly IDatabase _db;
    private readonly string _keyPrefix;
    private readonly ReceiptSearchRuntimeDescriptor _receiptSearchRuntime;
    private readonly string _receiptSearchDocumentIdPrefix;
    private readonly SemaphoreSlim _receiptSearchGate = new(1, 1);
    private readonly SemaphoreSlim _coverageSetupGate = new(1, 1);
    private SuperCenterModuleCapabilities _coverageCapabilities;
    private int _coverageSetupComplete;
    private int _receiptSearchSetupComplete;

    public StackExchangeSuperCenterProvider(
        IDatabase db,
        string? keyPrefix = null,
        ReceiptSearchRuntimeDescriptor? receiptSearchRuntime = null,
        string? receiptSearchDocumentIdPrefix = null)
    {
        _db = db;
        _keyPrefix = NormalizeKeyPrefix(keyPrefix);
        _receiptSearchRuntime = receiptSearchRuntime ?? ReceiptSearchRuntimeDescriptor.Default;
        _receiptSearchDocumentIdPrefix = NormalizeKeyPrefix(receiptSearchDocumentIdPrefix ?? keyPrefix);
    }

    public async ValueTask<Product?> GetProductAsync(string productId, CancellationToken ct)
    {
        var value = await _db.HashGetAsync(Key(SuperCenterKeySpace.Product(productId)), "payload").ConfigureAwait(false);
        if (!value.HasValue)
            return null;

        return JsonSerializer.Deserialize((byte[])value!, JsonContext.Product);
    }

    public async ValueTask CacheProductAsync(Product product, TimeSpan ttl, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(product, JsonContext.Product);
        await SetTaggedHashAsync(
            Key(SuperCenterKeySpace.Product(product.Id)),
            [
                new HashEntry("payload", payload),
                new HashEntry("category", product.Category),
                new HashEntry("department", product.Department),
                new HashEntry("aisle", product.Aisle),
                new HashEntry("price", product.Price.ToString(CultureInfo.InvariantCulture)),
                new HashEntry("temperatureZone", product.TemperatureZone)
            ],
            ttl,
            [
                $"product:{product.Id}",
                $"category:{product.Category.ToLowerInvariant()}",
                CacheTagConventions.ToZoneTag("catalog")
            ]).ConfigureAwait(false);
    }

    public async ValueTask<CartItem[]> GetCartAsync(string shopperId, CancellationToken ct)
    {
        var values = await _db.ListRangeAsync(Key(SuperCenterKeySpace.Cart(shopperId))).ConfigureAwait(false);
        if (values.Length == 0)
            return Array.Empty<CartItem>();

        var items = new CartItem[values.Length];
        for (var i = 0; i < values.Length; i++)
            items[i] = JsonSerializer.Deserialize((byte[])values[i]!, JsonContext.CartItem)!;

        return items;
    }

    public async ValueTask SetCartAsync(string shopperId, CartItem[] items, TimeSpan ttl, CancellationToken ct)
    {
        var key = Key(SuperCenterKeySpace.Cart(shopperId));
        _ = await _db.KeyDeleteAsync(key).ConfigureAwait(false);
        if (items.Length > 0)
        {
            var payloads = new RedisValue[items.Length];
            for (var i = 0; i < items.Length; i++)
                payloads[i] = JsonSerializer.SerializeToUtf8Bytes(items[i], JsonContext.CartItem);

            _ = await _db.ListRightPushAsync(key, payloads).ConfigureAwait(false);
        }

        _ = await _db.KeyExpireAsync(key, ttl).ConfigureAwait(false);
        await TrackTagsAsync(key, BuildCartTags(shopperId)).ConfigureAwait(false);
    }

    public ValueTask<long> GetCartCountAsync(string shopperId, CancellationToken ct)
        => new(_db.ListLengthAsync(Key(SuperCenterKeySpace.Cart(shopperId))));

    public async ValueTask RemoveCartAsync(string shopperId, CancellationToken ct)
        => _ = await _db.KeyDeleteAsync(Key(SuperCenterKeySpace.Cart(shopperId))).ConfigureAwait(false);

    public async ValueTask JoinFlashSaleAsync(string saleId, string shopperId, CancellationToken ct)
        => _ = await _db.SetAddAsync(Key(SuperCenterKeySpace.FlashSaleParticipants(saleId)), shopperId).ConfigureAwait(false);

    public ValueTask<bool> IsInFlashSaleAsync(string saleId, string shopperId, CancellationToken ct)
        => new(_db.SetContainsAsync(Key(SuperCenterKeySpace.FlashSaleParticipants(saleId)), shopperId));

    public ValueTask<long> GetFlashSaleParticipantCountAsync(string saleId, CancellationToken ct)
        => new(_db.SetLengthAsync(Key(SuperCenterKeySpace.FlashSaleParticipants(saleId))));

    public async ValueTask SaveSessionAsync(string sessionId, UserSession session, TimeSpan ttl, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(session, JsonContext.UserSession);
        await SetTaggedHashAsync(
            Key(SuperCenterKeySpace.Session(sessionId)),
            [
                new HashEntry("payload", payload),
                new HashEntry("shopperId", session.UserId),
                new HashEntry("storeId", session.StoreId),
                new HashEntry("loyaltyTier", session.LoyaltyTier),
                new HashEntry("fulfillmentMethod", session.FulfillmentMethod),
                new HashEntry("lastActivityTicks", session.LastActivityAt.Ticks.ToString(CultureInfo.InvariantCulture))
            ],
            ttl,
            [
                SuperCenterKeySpace.ShopperTag(session.UserId),
                SuperCenterKeySpace.ShopperTag(session.UserId, "session")
            ]).ConfigureAwait(false);
    }

    public async ValueTask<UserSession?> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        var value = await _db.HashGetAsync(Key(SuperCenterKeySpace.Session(sessionId)), "payload").ConfigureAwait(false);
        if (!value.HasValue)
            return null;

        return JsonSerializer.Deserialize((byte[])value!, JsonContext.UserSession);
    }

    public async ValueTask TrackRecentlyViewedAsync(
        string shopperId,
        IReadOnlyList<string> productIds,
        DateTime viewedAtUtc,
        TimeSpan ttl,
        CancellationToken ct)
    {
        if (productIds.Count == 0)
            return;

        var key = Key(SuperCenterKeySpace.RecentlyViewed(shopperId));
        var baseScore = new DateTimeOffset(viewedAtUtc).ToUnixTimeMilliseconds();
        for (var i = 0; i < productIds.Count; i++)
        {
            _ = await _db.SortedSetAddAsync(key, productIds[i], baseScore + i).ConfigureAwait(false);
        }

        _ = await _db.KeyExpireAsync(key, ttl).ConfigureAwait(false);
        await TrackTagsAsync(
            key,
            [
                SuperCenterKeySpace.ShopperTag(shopperId),
                SuperCenterKeySpace.ShopperTag(shopperId, "recent")
            ]).ConfigureAwait(false);
    }

    public async ValueTask<string[]> GetRecentlyViewedAsync(string shopperId, int take, CancellationToken ct)
    {
        if (take <= 0)
            return Array.Empty<string>();

        var values = await _db
            .SortedSetRangeByRankAsync(Key(SuperCenterKeySpace.RecentlyViewed(shopperId)), 0, take - 1, Order.Descending)
            .ConfigureAwait(false);
        if (values.Length == 0)
            return Array.Empty<string>();

        var products = new string[values.Length];
        for (var i = 0; i < values.Length; i++)
            products[i] = values[i].ToString();

        return products;
    }

    public async ValueTask RecordCheckoutEventAsync(GroceryCheckoutEvent checkoutEvent, TimeSpan ttl, CancellationToken ct)
    {
        var key = Key(SuperCenterKeySpace.CheckoutStream(checkoutEvent.ShopperId));
        _ = await _db.StreamAddAsync(
            key,
            [
                new NameValueEntry("orderId", checkoutEvent.OrderId),
                new NameValueEntry("shopperId", checkoutEvent.ShopperId),
                new NameValueEntry("sessionId", checkoutEvent.SessionId),
                new NameValueEntry("saleId", checkoutEvent.SaleId),
                new NameValueEntry("storeId", checkoutEvent.StoreId),
                new NameValueEntry("fulfillmentMethod", checkoutEvent.FulfillmentMethod),
                new NameValueEntry("itemCount", checkoutEvent.ItemCount.ToString(CultureInfo.InvariantCulture)),
                new NameValueEntry("subtotal", checkoutEvent.Subtotal.ToString(CultureInfo.InvariantCulture)),
                new NameValueEntry("checkedOutAtUtc", checkoutEvent.CheckedOutAtUtc.Ticks.ToString(CultureInfo.InvariantCulture))
            ]).ConfigureAwait(false);
        _ = await _db.KeyExpireAsync(key, ttl).ConfigureAwait(false);
        await EnsureReceiptSearchIndexAsync(ct).ConfigureAwait(false);
        await UpsertReceiptSearchDocumentAsync(checkoutEvent, ttl).ConfigureAwait(false);
    }

    public async ValueTask<ReceiptSearchLookup> SearchReceiptAsync(ReceiptExitCheckRequest request, CancellationToken ct)
    {
        await EnsureReceiptSearchIndexAsync(ct).ConfigureAwait(false);

        var result = await _db.ExecuteAsync(
            "FT.SEARCH",
            _receiptSearchRuntime.IndexName,
            BuildReceiptSearchQuery(request).RawQuery,
            "LIMIT",
            0,
            0).ConfigureAwait(false);

        var hitCount = ParseSearchCount(result);
        return new ReceiptSearchLookup(hitCount > 0, hitCount, GetReceiptSearchDocumentKey(request.OrderId));
    }

    public async ValueTask<bool> RemoveKeyAsync(string key, CancellationToken ct)
        => await _db.KeyDeleteAsync(ResolveDeleteKey(key)).ConfigureAwait(false);

    public async ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct)
    {
        var tagKey = Key($"tag:{tag}:keys");
        var tracked = await _db.SetMembersAsync(tagKey).ConfigureAwait(false);
        if (tracked.Length == 0)
        {
            await _db.KeyDeleteAsync(tagKey).ConfigureAwait(false);
            return 0;
        }

        var keys = new RedisKey[tracked.Length];
        for (var i = 0; i < tracked.Length; i++)
            keys[i] = tracked[i].ToString();

        var unlinkArgs = new object[keys.Length];
        for (var i = 0; i < keys.Length; i++)
            unlinkArgs[i] = keys[i];

        var deleted = await _db.ExecuteAsync("UNLINK", unlinkArgs).ConfigureAwait(false);
        await _db.KeyDeleteAsync(tagKey).ConfigureAwait(false);
        return (long)deleted;
    }

    public async ValueTask<SuperCenterCommandCoverageSnapshot> ExecuteCommandCoverageAsync(
        SuperCenterCommandCoverageContext context,
        CancellationToken ct)
    {
        var capabilities = await EnsureCoverageSetupAsync(ct).ConfigureAwait(false);
        long readOps = 0;
        long writeOps = 0;
        long adminOps = 0;
        long optionalSkips = 0;

        _ = await _db.ExecuteAsync("PING").ConfigureAwait(false);
        adminOps++;

        _ = await _db.ExecuteAsync("MODULE", "LIST").ConfigureAwait(false);
        adminOps++;

        var auditBase = Key($"audit:{context.ShopperId}");
        var profileKey = auditBase + ":profile";
        var windowKey = auditBase + ":window";
        var batchKeyA = auditBase + ":batch:a";
        var batchKeyB = auditBase + ":batch:b";
        var deleteKey = auditBase + ":delete";
        var hashKey = auditBase + ":hash";
        var listKey = auditBase + ":list";
        var setKey = auditBase + ":set";
        var zsetKey = auditBase + ":zset";
        var docKey = Key($"audit:search:doc:{context.ShopperId}");
        var jsonKey = auditBase + ":json";
        var bloomKey = auditBase + ":bloom";
        var seriesKey = auditBase + ":ts";
        var streamKey = auditBase + ":stream";

        var firstItem = context.Items.Length == 0
            ? new CartItem("fallback-item", "Fallback Item", 1m, 1, context.TimestampUtc)
            : context.Items[0];
        var secondItem = context.Items.Length > 1 ? context.Items[1] : firstItem;
        var profilePayload = Utf8.GetBytes($"{context.ShopperId}|{context.SaleId}");
        var windowPayload = Utf8.GetBytes($"{firstItem.ProductId}|{secondItem.ProductId}");

        _ = await _db.StringSetAsync(profileKey, profilePayload, CommandCoverageTtl).ConfigureAwait(false);
        writeOps++;
        _ = await _db.StringGetAsync(profileKey).ConfigureAwait(false);
        readOps++;
        _ = await _db.ExecuteAsync("GETEX", profileKey, "PX", 120_000).ConfigureAwait(false);
        readOps++;
        _ = await _db.ExecuteAsync("TTL", profileKey).ConfigureAwait(false);
        readOps++;
        _ = await _db.ExecuteAsync("PTTL", profileKey).ConfigureAwait(false);
        readOps++;
        _ = await _db.KeyExpireAsync(profileKey, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        writeOps++;

        _ = await _db.StringSetAsync(windowKey, windowPayload, CommandCoverageTtl).ConfigureAwait(false);
        writeOps++;
        _ = await _db.StringGetRangeAsync(windowKey, 0, 0).ConfigureAwait(false);
        readOps++;

        _ = await _db.StringSetAsync(
            [
                new KeyValuePair<RedisKey, RedisValue>(batchKeyA, firstItem.ProductId),
                new KeyValuePair<RedisKey, RedisValue>(batchKeyB, secondItem.ProductName)
            ]).ConfigureAwait(false);
        writeOps++;
        _ = await _db.StringGetAsync([batchKeyA, batchKeyB]).ConfigureAwait(false);
        readOps++;

        _ = await _db.StringSetAsync(deleteKey, "cleanup", CommandCoverageTtl).ConfigureAwait(false);
        writeOps++;
        _ = await _db.KeyDeleteAsync(deleteKey).ConfigureAwait(false);
        writeOps++;

        await _db.HashSetAsync(
            hashKey,
            [
                new HashEntry("shopper", context.ShopperId),
                new HashEntry("sale", context.SaleId),
                new HashEntry("product", firstItem.ProductId)
            ]).ConfigureAwait(false);
        writeOps++;
        _ = await _db.HashGetAsync(hashKey, "shopper").ConfigureAwait(false);
        readOps++;
        _ = await _db.HashGetAsync(hashKey, ["shopper", "sale", "product"]).ConfigureAwait(false);
        readOps++;

        _ = await _db.ListLeftPushAsync(listKey, firstItem.ProductId).ConfigureAwait(false);
        writeOps++;
        _ = await _db.ListRightPushAsync(listKey, secondItem.ProductId).ConfigureAwait(false);
        writeOps++;
        _ = await _db.ListRightPushAsync(listKey, [context.SessionId, context.SaleId]).ConfigureAwait(false);
        writeOps++;
        _ = await _db.ListGetByIndexAsync(listKey, 0).ConfigureAwait(false);
        readOps++;
        _ = await _db.ListRangeAsync(listKey, 0, -1).ConfigureAwait(false);
        readOps++;
        _ = await _db.ListLengthAsync(listKey).ConfigureAwait(false);
        readOps++;
        _ = await _db.ListLeftPopAsync(listKey).ConfigureAwait(false);
        readOps++;
        _ = await _db.ListRightPopAsync(listKey).ConfigureAwait(false);
        readOps++;

        _ = await _db.SetAddAsync(setKey, context.ShopperId).ConfigureAwait(false);
        writeOps++;
        _ = await _db.SetAddAsync(setKey, firstItem.ProductId).ConfigureAwait(false);
        writeOps++;
        _ = await _db.SetContainsAsync(setKey, context.ShopperId).ConfigureAwait(false);
        readOps++;
        _ = await _db.SetMembersAsync(setKey).ConfigureAwait(false);
        readOps++;
        _ = await _db.SetLengthAsync(setKey).ConfigureAwait(false);
        readOps++;
        _ = await _db.SetRemoveAsync(setKey, firstItem.ProductId).ConfigureAwait(false);
        writeOps++;

        _ = await _db.SortedSetAddAsync(zsetKey, firstItem.ProductId, firstItem.Quantity).ConfigureAwait(false);
        writeOps++;
        _ = await _db.SortedSetAddAsync(zsetKey, secondItem.ProductId, secondItem.Quantity + 0.5d).ConfigureAwait(false);
        writeOps++;
        _ = await _db.SortedSetScoreAsync(zsetKey, firstItem.ProductId).ConfigureAwait(false);
        readOps++;
        _ = await _db.SortedSetRankAsync(zsetKey, secondItem.ProductId, Order.Ascending).ConfigureAwait(false);
        readOps++;
        _ = await _db.SortedSetIncrementAsync(zsetKey, firstItem.ProductId, 1d).ConfigureAwait(false);
        writeOps++;
        _ = await _db.SortedSetRangeByRankWithScoresAsync(zsetKey, 0, -1, Order.Ascending).ConfigureAwait(false);
        readOps++;
        _ = await _db.SortedSetRangeByScoreWithScoresAsync(zsetKey, double.NegativeInfinity, double.PositiveInfinity, Exclude.None, Order.Ascending, 0, 10).ConfigureAwait(false);
        readOps++;
        _ = await _db.SortedSetLengthAsync(zsetKey).ConfigureAwait(false);
        readOps++;
        _ = await _db.SortedSetRemoveAsync(zsetKey, secondItem.ProductId).ConfigureAwait(false);
        writeOps++;

        if (capabilities.SupportsJson)
        {
            var jsonPayload = Utf8.GetBytes(
                $"{{\"u\":\"{context.ShopperId}\",\"s\":\"{context.SaleId}\",\"p\":\"{firstItem.ProductId}\",\"n\":{context.Items.Length}}}");
            _ = await _db.ExecuteAsync("JSON.SET", jsonKey, ".", jsonPayload).ConfigureAwait(false);
            writeOps++;
            _ = await _db.ExecuteAsync("JSON.GET", jsonKey, ".u").ConfigureAwait(false);
            readOps++;
            _ = await _db.ExecuteAsync("JSON.DEL", jsonKey, ".").ConfigureAwait(false);
            writeOps++;
        }
        else
        {
            optionalSkips += 3;
        }

        if (capabilities.SupportsSearch)
        {
            await _db.HashSetAsync(
                docKey,
                [
                    new HashEntry("shopper", context.ShopperId),
                    new HashEntry("product", firstItem.ProductId),
                    new HashEntry("sale", context.SaleId)
                ]).ConfigureAwait(false);
            writeOps++;
            _ = await _db.ExecuteAsync("FT.SEARCH", CoverageIndexName(), context.ShopperId, "LIMIT", 0, 0).ConfigureAwait(false);
            readOps++;
        }
        else
        {
            optionalSkips += 4;
        }

        if (capabilities.SupportsBloom)
        {
            _ = await _db.ExecuteAsync("BF.ADD", bloomKey, context.SessionId).ConfigureAwait(false);
            writeOps++;
            _ = await _db.ExecuteAsync("BF.EXISTS", bloomKey, context.SessionId).ConfigureAwait(false);
            readOps++;
        }
        else
        {
            optionalSkips += 2;
        }

        if (capabilities.SupportsTimeSeries)
        {
            _ = await _db.ExecuteAsync("TS.CREATE", seriesKey).ConfigureAwait(false);
            writeOps++;
            var ts = new DateTimeOffset(context.TimestampUtc).ToUnixTimeMilliseconds();
            _ = await _db.ExecuteAsync("TS.ADD", seriesKey, ts, context.Items.Length).ConfigureAwait(false);
            writeOps++;
            _ = await _db.ExecuteAsync("TS.RANGE", seriesKey, ts, ts).ConfigureAwait(false);
            readOps++;
        }
        else
        {
            optionalSkips += 3;
        }

        if (capabilities.SupportsIdempotentStreams)
        {
            _ = await _db.ExecuteAsync("XADD", streamKey, "IDMP", "grocery-bench", "evt:" + context.ShopperId, "*", "shopper", context.ShopperId, "sale", context.SaleId).ConfigureAwait(false);
            writeOps++;
            _ = await _db.ExecuteAsync("XCFGSET", streamKey, "IDMP-DURATION", 600, "IDMP-MAXSIZE", 128).ConfigureAwait(false);
            adminOps++;
        }
        else
        {
            optionalSkips += 2;
        }

        _ = await _db.ExecuteAsync(
            "UNLINK",
            hashKey,
            listKey,
            setKey,
            zsetKey,
            profileKey,
            windowKey,
            batchKeyA,
            batchKeyB,
            docKey,
            jsonKey,
            bloomKey,
            seriesKey,
            streamKey).ConfigureAwait(false);
        writeOps++;

        return new SuperCenterCommandCoverageSnapshot(readOps, writeOps, adminOps, optionalSkips);
    }

    private async ValueTask<SuperCenterModuleCapabilities> EnsureCoverageSetupAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _coverageSetupComplete) != 0)
            return _coverageCapabilities;

        await _coverageSetupGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _coverageSetupComplete) == 0)
            {
                _coverageCapabilities = await DiscoverCoverageCapabilitiesAsync(ct).ConfigureAwait(false);
                if (_coverageCapabilities.SupportsSearch)
                    await EnsureSearchIndexAsync().ConfigureAwait(false);

                Volatile.Write(ref _coverageSetupComplete, 1);
            }

            return _coverageCapabilities;
        }
        finally
        {
            _coverageSetupGate.Release();
        }
    }

    private async ValueTask<SuperCenterModuleCapabilities> DiscoverCoverageCapabilitiesAsync(CancellationToken ct)
    {
        var modules = await _db.ExecuteAsync("MODULE", "LIST").ConfigureAwait(false);
        var names = ParseModuleNames(modules);
        var supportsJson = false;
        var supportsSearch = false;
        var supportsBloom = false;
        var supportsTimeSeries = false;

        for (var i = 0; i < names.Length; i++)
        {
            var module = names[i];
            if (module.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                module.Contains("rejson", StringComparison.OrdinalIgnoreCase))
            {
                supportsJson = true;
            }

            if (module.Contains("search", StringComparison.OrdinalIgnoreCase) ||
                module.Contains("ft", StringComparison.OrdinalIgnoreCase))
            {
                supportsSearch = true;
            }

            if (module.Contains("bloom", StringComparison.OrdinalIgnoreCase) ||
                module.Contains("bf", StringComparison.OrdinalIgnoreCase))
            {
                supportsBloom = true;
            }

            if (module.Contains("timeseries", StringComparison.OrdinalIgnoreCase) ||
                module.Contains("ts", StringComparison.OrdinalIgnoreCase))
            {
                supportsTimeSeries = true;
            }
        }

        var supportsIdempotentStreams = await SupportsIdempotentStreamsAsync().ConfigureAwait(false);
        return new SuperCenterModuleCapabilities(
            supportsJson,
            supportsSearch,
            supportsBloom,
            supportsTimeSeries,
            supportsIdempotentStreams);
    }

    private async ValueTask EnsureSearchIndexAsync()
    {
        try
        {
            _ = await _db.ExecuteAsync(
                "FT.CREATE",
                CoverageIndexName(),
                "ON", "HASH",
                "PREFIX", 1, Key("audit:search:doc:"),
                "SCHEMA",
                "shopper", "TEXT",
                "product", "TEXT",
                "sale", "TEXT").ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("Index already exists", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private string CoverageIndexName()
        => Key("audit:search:index");

    private static string[] ParseModuleNames(RedisResult result)
    {
        if (result.IsNull)
            return [];

        var rows = (RedisResult[])result!;
        var names = new List<string>(rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            if (rows[i].IsNull)
                continue;

            var parts = (RedisResult[])rows[i]!;
            for (var partIndex = 0; partIndex + 1 < parts.Length; partIndex += 2)
            {
                var key = parts[partIndex].ToString();
                if (!string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = parts[partIndex + 1].ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
                break;
            }
        }

        return names.ToArray();
    }

    private async ValueTask EnsureReceiptSearchIndexAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _receiptSearchSetupComplete) != 0)
            return;

        await _receiptSearchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _receiptSearchSetupComplete) != 0)
                return;

            var modules = await _db.ExecuteAsync("MODULE", "LIST").ConfigureAwait(false);
            var names = ParseModuleNames(modules);
            var searchAvailable = false;
            for (var i = 0; i < names.Length; i++)
            {
                var module = names[i];
                if (module.Contains("search", StringComparison.OrdinalIgnoreCase) ||
                    module.Contains("ft", StringComparison.OrdinalIgnoreCase))
                {
                    searchAvailable = true;
                    break;
                }
            }

            if (!searchAvailable)
            {
                throw new InvalidOperationException(
                    $"RediSearch is required for grocery receipt search index '{_receiptSearchRuntime.IndexName}'.");
            }

            try
            {
                _ = await _db.ExecuteAsync("FT.CREATE", BuildReceiptSearchCreateIndexArgs()).ConfigureAwait(false);
            }
            catch (RedisServerException ex) when (ex.Message.Contains("Index already exists", StringComparison.OrdinalIgnoreCase))
            {
            }

            Volatile.Write(ref _receiptSearchSetupComplete, 1);
        }
        finally
        {
            _receiptSearchGate.Release();
        }
    }

    private async ValueTask UpsertReceiptSearchDocumentAsync(GroceryCheckoutEvent checkoutEvent, TimeSpan ttl)
    {
        var document = SuperCenterReceiptSearch.CreateDocument(checkoutEvent, _receiptSearchDocumentIdPrefix);
        var key = GetReceiptSearchDocumentKey(document.OrderId);
        await _db.HashSetAsync(
            key,
            [
                new HashEntry("orderId", document.OrderId),
                new HashEntry("shopperId", document.ShopperId),
                new HashEntry("sessionId", document.SessionId),
                new HashEntry("saleId", document.SaleId),
                new HashEntry("storeId", document.StoreId),
                new HashEntry("receiptStatus", document.ReceiptStatus),
                new HashEntry("fulfillmentMethod", document.FulfillmentMethod),
                new HashEntry("runScope", document.RunScope),
                new HashEntry("itemCount", document.ItemCount.ToString(CultureInfo.InvariantCulture)),
                new HashEntry("subtotal", document.Subtotal.ToString(CultureInfo.InvariantCulture)),
                new HashEntry("checkedOutUnixMilliseconds", document.CheckedOutUnixMilliseconds.ToString(CultureInfo.InvariantCulture)),
                new HashEntry("searchText", document.SearchText)
            ]).ConfigureAwait(false);
        _ = await _db.KeyExpireAsync(key, ttl).ConfigureAwait(false);
    }

    private object[] BuildReceiptSearchCreateIndexArgs()
    {
        var fields = new ReceiptSearchDocumentMapper(_receiptSearchRuntime).Index.Fields;
        var args = new List<object>(8 + (fields.Count * 4))
        {
            _receiptSearchRuntime.IndexName,
            "ON",
            "HASH",
            "PREFIX",
            1,
            _receiptSearchRuntime.DocumentKeyPrefix,
            "SCHEMA"
        };

        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            args.Add(field.Name);
            if (!string.IsNullOrWhiteSpace(field.Alias))
            {
                args.Add("AS");
                args.Add(field.Alias!);
            }

            switch (field.Type)
            {
                case RedisSearchFieldType.Tag:
                    args.Add("TAG");
                    if (!string.IsNullOrWhiteSpace(field.TagSeparator))
                    {
                        args.Add("SEPARATOR");
                        args.Add(field.TagSeparator!);
                    }
                    break;
                case RedisSearchFieldType.Numeric:
                    args.Add("NUMERIC");
                    break;
                default:
                    args.Add("TEXT");
                    if (field.Weight.HasValue)
                    {
                        args.Add("WEIGHT");
                        args.Add(field.Weight.Value.ToString(CultureInfo.InvariantCulture));
                    }
                    break;
            }

            if (field.Sortable)
                args.Add("SORTABLE");
        }

        return [.. args];
    }

    private RedisSearchQuery BuildReceiptSearchQuery(ReceiptExitCheckRequest request)
    {
        var builder = new RedisSearchQueryBuilder()
            .Tag("orderId", request.OrderId)
            .Tag("shopperId", request.ShopperId)
            .Tag("storeId", request.StoreId)
            .Tag("receiptStatus", request.ReceiptStatus)
            .NumericRange(
                "checkedOutUnixMilliseconds",
                min: new DateTimeOffset(request.CheckedOutAfterUtc).ToUnixTimeMilliseconds());

        if (!string.IsNullOrWhiteSpace(_receiptSearchDocumentIdPrefix))
            builder.Tag("runScope", _receiptSearchDocumentIdPrefix);

        return builder.Build(offset: 0, count: Math.Clamp(request.Take, 1, 10));
    }

    private string GetReceiptSearchDocumentKey(string orderId)
        => string.Concat(_receiptSearchRuntime.DocumentKeyPrefix, _receiptSearchDocumentIdPrefix, orderId);

    private string ResolveDeleteKey(string key)
        => SuperCenterReceiptSearch.IsAbsoluteSearchDocumentKey(key) ? key : Key(key);

    private static long ParseSearchCount(RedisResult result)
    {
        if (result.IsNull)
            return 0L;

        if (long.TryParse(result.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var direct))
            return direct;

        try
        {
            var values = (RedisResult[])result!;
            if (values.Length == 0 || values[0].IsNull)
                return 0L;

            if (long.TryParse(values[0].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        catch (InvalidCastException)
        {
        }

        return 0L;
    }

    private async ValueTask<bool> SupportsIdempotentStreamsAsync()
    {
        var probeKey = Key("audit:stream:probe");
        try
        {
            _ = await _db.ExecuteAsync("XADD", probeKey, "IDMP", "probe", "probe-1", "*", "probe", "1").ConfigureAwait(false);
            _ = await _db.ExecuteAsync("XCFGSET", probeKey, "IDMP-DURATION", 60, "IDMP-MAXSIZE", 4).ConfigureAwait(false);
            _ = await _db.ExecuteAsync("UNLINK", probeKey).ConfigureAwait(false);
            return true;
        }
        catch (RedisServerException ex) when (
            ex.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("unknown subcommand", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("invalid stream id", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
    }

    private async ValueTask SetTaggedHashAsync(string key, HashEntry[] entries, TimeSpan ttl, IReadOnlyCollection<string> tags)
    {
        await _db.HashSetAsync(key, entries).ConfigureAwait(false);
        await _db.KeyExpireAsync(key, ttl).ConfigureAwait(false);
        await TrackTagsAsync(key, tags).ConfigureAwait(false);
    }

    private async ValueTask TrackTagsAsync(string key, IReadOnlyCollection<string> tags)
    {
        foreach (var tag in tags)
            await _db.SetAddAsync(Key($"tag:{tag}:keys"), key).ConfigureAwait(false);
    }

    private static string[] BuildCartTags(string shopperId)
        =>
        [
            SuperCenterKeySpace.ShopperTag(shopperId),
            SuperCenterKeySpace.ShopperTag(shopperId, "cart")
        ];

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
