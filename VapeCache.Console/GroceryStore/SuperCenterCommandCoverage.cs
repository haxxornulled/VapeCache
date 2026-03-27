namespace VapeCache.Console.GroceryStore;

public interface IGroceryStoreCommandCoverageRunner
{
    ValueTask ExecuteShopperCommandCoverageAsync(
        string shopperId,
        string saleId,
        string sessionId,
        DateTime timestampUtc,
        CartItem[] items,
        CancellationToken ct = default);
}

internal readonly struct SuperCenterCommandCoverageContext
{
    public SuperCenterCommandCoverageContext(
        string shopperId,
        string saleId,
        string sessionId,
        DateTime timestampUtc,
        CartItem[] items)
    {
        ShopperId = shopperId;
        SaleId = saleId;
        SessionId = sessionId;
        TimestampUtc = timestampUtc;
        Items = items;
    }

    public string ShopperId { get; }
    public string SaleId { get; }
    public string SessionId { get; }
    public DateTime TimestampUtc { get; }
    public CartItem[] Items { get; }
}

internal readonly struct SuperCenterCommandCoverageSnapshot
{
    public SuperCenterCommandCoverageSnapshot(
        long readOps,
        long writeOps,
        long adminOps,
        long optionalSkips)
    {
        ReadOps = readOps;
        WriteOps = writeOps;
        AdminOps = adminOps;
        OptionalSkips = optionalSkips;
    }

    public long ReadOps { get; }
    public long WriteOps { get; }
    public long AdminOps { get; }
    public long OptionalSkips { get; }
}

internal readonly struct SuperCenterModuleCapabilities
{
    public SuperCenterModuleCapabilities(
        bool supportsJson,
        bool supportsSearch,
        bool supportsBloom,
        bool supportsTimeSeries,
        bool supportsIdempotentStreams)
    {
        SupportsJson = supportsJson;
        SupportsSearch = supportsSearch;
        SupportsBloom = supportsBloom;
        SupportsTimeSeries = supportsTimeSeries;
        SupportsIdempotentStreams = supportsIdempotentStreams;
    }

    public bool SupportsJson { get; }
    public bool SupportsSearch { get; }
    public bool SupportsBloom { get; }
    public bool SupportsTimeSeries { get; }
    public bool SupportsIdempotentStreams { get; }
}
