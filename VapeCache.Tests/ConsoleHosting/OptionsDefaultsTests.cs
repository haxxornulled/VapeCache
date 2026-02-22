using VapeCache.Console.Hosting;
using VapeCache.Console.GroceryStore;
using VapeCache.Console.Plugins;

namespace VapeCache.Tests.ConsoleHosting;

public sealed class OptionsDefaultsTests
{
    [Fact]
    public void StartupPreflightOptions_has_expected_defaults()
    {
        var o = new StartupPreflightOptions();

        Assert.False(o.Enabled);
        Assert.True(o.FailFast);
        Assert.True(o.ValidatePing);
        Assert.True(o.FailoverToMemoryOnFailure);
        Assert.True(o.SanityCheckEnabled);
        Assert.True(o.Connections >= 1);
    }

    [Fact]
    public void LiveDemoOptions_has_expected_defaults()
    {
        var o = new LiveDemoOptions();

        Assert.True(o.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(o.Key));
        Assert.True(o.Interval > TimeSpan.Zero);
        Assert.True(o.Ttl > TimeSpan.Zero);
    }

    [Fact]
    public void PluginDemoOptions_has_expected_defaults()
    {
        var o = new PluginDemoOptions();

        Assert.False(o.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(o.KeyPrefix));
        Assert.True(o.Ttl > TimeSpan.Zero);
    }

    [Fact]
    public void GroceryStoreStressOptions_has_expected_defaults()
    {
        var o = new GroceryStoreStressOptions();

        Assert.True(o.Enabled);
        Assert.True(o.ConcurrentShoppers > 0);
        Assert.True(o.TotalShoppers > 0);
        Assert.True(o.TargetDurationSeconds > 0);
        Assert.True(o.StartupDelaySeconds >= 0);
        Assert.True(o.CountdownSeconds >= 0);
        Assert.InRange(o.BrowseChancePercent, 0, 100);
        Assert.True(o.BrowseMinProducts >= 0);
        Assert.True(o.BrowseMaxProducts >= o.BrowseMinProducts);
        Assert.InRange(o.FlashSaleJoinChancePercent, 0, 100);
        Assert.InRange(o.AddToCartChancePercent, 0, 100);
        Assert.True(o.CartItemsMin >= 0);
        Assert.True(o.CartItemsMax >= o.CartItemsMin);
        Assert.True(o.CartItemQuantityMin >= 1);
        Assert.True(o.CartItemQuantityMax >= o.CartItemQuantityMin);
        Assert.InRange(o.ViewCartChancePercent, 0, 100);
        Assert.InRange(o.CheckoutChancePercent, 0, 100);
        Assert.InRange(o.RemoveFromCartChancePercent, 0, 100);
        Assert.True(o.StatsIntervalSeconds > 0);
    }
}
