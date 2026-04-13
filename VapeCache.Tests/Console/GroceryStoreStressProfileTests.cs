using System.Reflection;
using VapeCache.Console.GroceryStore;

namespace VapeCache.Tests.Console;

public sealed class GroceryStoreStressProfileTests
{
    [Theory]
    [InlineData("dogfood", 65, 8, 16, 25, 45, 8, 16, 1, 6, 25, 15, 8, 10)]
    [InlineData("showcase", 70, 10, 25, 30, 50, 30, 50, 1, 10, 30, 20, 10, 10)]
    [InlineData("stampede", 100, 1, 1, 0, 100, 70, 70, 1, 1, 0, 0, 0, 10)]
    public void Normalize_applies_named_workload_profile(
        string profile,
        int browseChancePercent,
        int browseMinProducts,
        int browseMaxProducts,
        int flashSaleJoinChancePercent,
        int addToCartChancePercent,
        int cartItemsMin,
        int cartItemsMax,
        int cartItemQuantityMin,
        int cartItemQuantityMax,
        int viewCartChancePercent,
        int checkoutChancePercent,
        int removeFromCartChancePercent,
        int statsIntervalSeconds)
    {
        var options = new GroceryStoreStressOptions
        {
            WorkloadProfile = profile,
            BrowseChancePercent = 1,
            BrowseMinProducts = 2,
            BrowseMaxProducts = 3,
            FlashSaleJoinChancePercent = 4,
            AddToCartChancePercent = 5,
            CartItemsMin = 6,
            CartItemsMax = 7,
            CartItemQuantityMin = 8,
            CartItemQuantityMax = 9,
            ViewCartChancePercent = 10,
            CheckoutChancePercent = 11,
            RemoveFromCartChancePercent = 12,
            StatsIntervalSeconds = 13
        };

        var workload = InvokeNormalize(options);

        Assert.Equal(browseChancePercent, ReadInt(workload, "BrowseChancePercent"));
        Assert.Equal(browseMinProducts, ReadInt(workload, "BrowseMinProducts"));
        Assert.Equal(browseMaxProducts, ReadInt(workload, "BrowseMaxProducts"));
        Assert.Equal(flashSaleJoinChancePercent, ReadInt(workload, "FlashSaleJoinChancePercent"));
        Assert.Equal(addToCartChancePercent, ReadInt(workload, "AddToCartChancePercent"));
        Assert.Equal(cartItemsMin, ReadInt(workload, "CartItemsMin"));
        Assert.Equal(cartItemsMax, ReadInt(workload, "CartItemsMax"));
        Assert.Equal(cartItemQuantityMin, ReadInt(workload, "CartItemQuantityMin"));
        Assert.Equal(cartItemQuantityMax, ReadInt(workload, "CartItemQuantityMax"));
        Assert.Equal(viewCartChancePercent, ReadInt(workload, "ViewCartChancePercent"));
        Assert.Equal(checkoutChancePercent, ReadInt(workload, "CheckoutChancePercent"));
        Assert.Equal(removeFromCartChancePercent, ReadInt(workload, "RemoveFromCartChancePercent"));
        Assert.Equal(statsIntervalSeconds, ReadTimeSpanSeconds(workload, "StatsInterval"));
    }

    private static object InvokeNormalize(GroceryStoreStressOptions options)
    {
        var method = typeof(GroceryStoreStressTest).GetMethod("Normalize", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!.Invoke(null, [options])!;
    }

    private static int ReadInt(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);
        return (int)property!.GetValue(instance)!;
    }

    private static int ReadTimeSpanSeconds(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);
        return (int)((TimeSpan)property!.GetValue(instance)!).TotalSeconds;
    }
}
