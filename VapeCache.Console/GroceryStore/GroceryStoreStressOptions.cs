namespace VapeCache.Console.GroceryStore;

public sealed class GroceryStoreStressOptions
{
    public bool Enabled { get; init; } = true;
    public int ConcurrentShoppers { get; init; } = 2000;
    public int TotalShoppers { get; init; } = 100000;
    public int TargetDurationSeconds { get; init; } = 180;
    public int StartupDelaySeconds { get; init; } = 5;
    public int CountdownSeconds { get; init; } = 3;

    public int BrowseChancePercent { get; init; } = 70;
    public int BrowseMinProducts { get; init; } = 10;
    public int BrowseMaxProducts { get; init; } = 25;

    public int FlashSaleJoinChancePercent { get; init; } = 30;

    public int AddToCartChancePercent { get; init; } = 50;
    public int CartItemsMin { get; init; } = 15;
    public int CartItemsMax { get; init; } = 35;
    public int CartItemQuantityMin { get; init; } = 1;
    public int CartItemQuantityMax { get; init; } = 10;

    public int ViewCartChancePercent { get; init; } = 30;
    public int CheckoutChancePercent { get; init; } = 20;
    public int RemoveFromCartChancePercent { get; init; } = 10;

    public int StatsIntervalSeconds { get; init; } = 10;
    public bool StopHostOnCompletion { get; init; } = true;

    // Optional hot-key stampede controls.
    // When set, product selection can be biased toward a single product id.
    public string? HotProductId { get; init; }
    public int HotProductBiasPercent { get; init; } = 0;
    public bool ForceHotProductFlashSale { get; init; } = false;
}
