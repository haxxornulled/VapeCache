namespace VapeCache.Console.GroceryStore;

public readonly record struct GroceryStoreComparisonTelemetrySnapshot(
    long ProductReadOps,
    long ProductWriteOps,
    long CartReadOps,
    long CartCountReadOps,
    long CartItemWriteOps,
    long CartClearWriteOps,
    long FlashSaleJoinWriteOps,
    long FlashSaleMembershipReadOps,
    long FlashSaleParticipantCountReadOps,
    long SessionReadOps,
    long SessionWriteOps)
{
    public long ReadOps =>
        ProductReadOps +
        CartReadOps +
        CartCountReadOps +
        FlashSaleMembershipReadOps +
        FlashSaleParticipantCountReadOps +
        SessionReadOps;

    public long WriteOps =>
        ProductWriteOps +
        CartItemWriteOps +
        CartClearWriteOps +
        FlashSaleJoinWriteOps +
        SessionWriteOps;

    public long TotalOps => ReadOps + WriteOps;
}

public interface IGroceryStoreComparisonTelemetrySource
{
    GroceryStoreComparisonTelemetrySnapshot GetTelemetrySnapshot();
}
