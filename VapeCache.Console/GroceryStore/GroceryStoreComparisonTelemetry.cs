namespace VapeCache.Console.GroceryStore;

public readonly record struct GroceryStoreComparisonTelemetrySnapshot
{
    public GroceryStoreComparisonTelemetrySnapshot(
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
        long SessionWriteOps,
        long RecentlyViewedReadOps = 0,
        long RecentlyViewedWriteOps = 0,
        long CheckoutEventWriteOps = 0,
        long ReceiptSearchReadOps = 0,
        long ReceiptReviewInvalidationWriteOps = 0,
        long TagInvalidationWriteOps = 0,
        long CommandCoverageReadOps = 0,
        long CommandCoverageWriteOps = 0,
        long CommandCoverageAdminOps = 0,
        long CommandCoverageOptionalSkips = 0)
    {
        this.ProductReadOps = ProductReadOps;
        this.ProductWriteOps = ProductWriteOps;
        this.CartReadOps = CartReadOps;
        this.CartCountReadOps = CartCountReadOps;
        this.CartItemWriteOps = CartItemWriteOps;
        this.CartClearWriteOps = CartClearWriteOps;
        this.FlashSaleJoinWriteOps = FlashSaleJoinWriteOps;
        this.FlashSaleMembershipReadOps = FlashSaleMembershipReadOps;
        this.FlashSaleParticipantCountReadOps = FlashSaleParticipantCountReadOps;
        this.SessionReadOps = SessionReadOps;
        this.SessionWriteOps = SessionWriteOps;
        this.RecentlyViewedReadOps = RecentlyViewedReadOps;
        this.RecentlyViewedWriteOps = RecentlyViewedWriteOps;
        this.CheckoutEventWriteOps = CheckoutEventWriteOps;
        this.ReceiptSearchReadOps = ReceiptSearchReadOps;
        this.ReceiptReviewInvalidationWriteOps = ReceiptReviewInvalidationWriteOps;
        this.TagInvalidationWriteOps = TagInvalidationWriteOps;
        this.CommandCoverageReadOps = CommandCoverageReadOps;
        this.CommandCoverageWriteOps = CommandCoverageWriteOps;
        this.CommandCoverageAdminOps = CommandCoverageAdminOps;
        this.CommandCoverageOptionalSkips = CommandCoverageOptionalSkips;
    }

    public long ProductReadOps { get; init; }
    public long ProductWriteOps { get; init; }
    public long CartReadOps { get; init; }
    public long CartCountReadOps { get; init; }
    public long CartItemWriteOps { get; init; }
    public long CartClearWriteOps { get; init; }
    public long FlashSaleJoinWriteOps { get; init; }
    public long FlashSaleMembershipReadOps { get; init; }
    public long FlashSaleParticipantCountReadOps { get; init; }
    public long SessionReadOps { get; init; }
    public long SessionWriteOps { get; init; }
    public long RecentlyViewedReadOps { get; init; }
    public long RecentlyViewedWriteOps { get; init; }
    public long CheckoutEventWriteOps { get; init; }
    public long ReceiptSearchReadOps { get; init; }
    public long ReceiptReviewInvalidationWriteOps { get; init; }
    public long TagInvalidationWriteOps { get; init; }
    public long CommandCoverageReadOps { get; init; }
    public long CommandCoverageWriteOps { get; init; }
    public long CommandCoverageAdminOps { get; init; }
    public long CommandCoverageOptionalSkips { get; init; }

    public long ReadOps =>
        ProductReadOps +
        CartReadOps +
        CartCountReadOps +
        FlashSaleMembershipReadOps +
        FlashSaleParticipantCountReadOps +
        SessionReadOps +
        RecentlyViewedReadOps +
        ReceiptSearchReadOps +
        CommandCoverageReadOps;

    public long WriteOps =>
        ProductWriteOps +
        CartItemWriteOps +
        CartClearWriteOps +
        FlashSaleJoinWriteOps +
        SessionWriteOps +
        RecentlyViewedWriteOps +
        CheckoutEventWriteOps +
        ReceiptReviewInvalidationWriteOps +
        TagInvalidationWriteOps +
        CommandCoverageWriteOps;

    public long AdminOps => CommandCoverageAdminOps;

    public long OptionalSkips => CommandCoverageOptionalSkips;

    public long TotalOps => ReadOps + WriteOps + AdminOps;
}

public interface IGroceryStoreComparisonTelemetrySource
{
    GroceryStoreComparisonTelemetrySnapshot GetTelemetrySnapshot();
}
