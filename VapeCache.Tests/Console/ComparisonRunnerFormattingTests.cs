using System.Reflection;
using VapeCache.Console.GroceryStore;

namespace VapeCache.Tests.Console;

[Collection(ConsoleIoCollection.Name)]
public sealed class ComparisonRunnerFormattingTests
{
    [Fact]
    public void PrintComparison_writes_human_readable_summary()
    {
        var vape = new StressTestResult(
            ProviderName: "VapeCache",
            ShopperCount: 10_000,
            SuccessCount: 10_000,
            ErrorCount: 0,
            TotalDuration: TimeSpan.FromSeconds(10),
            ShopperDuration: TimeSpan.FromSeconds(8),
            PreCacheDuration: TimeSpan.FromMilliseconds(50),
            AverageCartSize: 20m,
            AverageLatencyMs: 1.2m,
            P50LatencyMs: 1.0m,
            P95LatencyMs: 2.2m,
            P99LatencyMs: 3.1m,
            P999LatencyMs: 4.1m,
            ThroughputShoppersPerSec: 1_250m,
            AllocatedBytes: 1_000_000,
            Gen0Collections: 1,
            Gen1Collections: 0,
            Gen2Collections: 0);

        var ser = new StressTestResult(
            ProviderName: "StackExchange.Redis",
            ShopperCount: 10_000,
            SuccessCount: 9_999,
            ErrorCount: 1,
            TotalDuration: TimeSpan.FromSeconds(12),
            ShopperDuration: TimeSpan.FromSeconds(10),
            PreCacheDuration: TimeSpan.FromMilliseconds(80),
            AverageCartSize: 20m,
            AverageLatencyMs: 2.0m,
            P50LatencyMs: 1.8m,
            P95LatencyMs: 3.0m,
            P99LatencyMs: 4.5m,
            P999LatencyMs: 6.4m,
            ThroughputShoppersPerSec: 1_000m,
            AllocatedBytes: 2_000_000,
            Gen0Collections: 2,
            Gen1Collections: 1,
            Gen2Collections: 0);

        var previousOut = System.Console.Out;
        try
        {
            using var output = new StringWriter();
            System.Console.SetOut(output);

            InvokePrivate(nameof(ComparisonRunner), "PrintComparison", vape, ser);

            var text = output.ToString();
            Assert.Contains("Throughput (shoppers/sec)", text);
            Assert.Contains("VapeCache is", text);
            Assert.Contains("LOWER average latency", text);
        }
        finally
        {
            System.Console.SetOut(previousOut);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PrintMetric_handles_higher_and_lower_is_better(bool higher)
    {
        var previousOut = System.Console.Out;
        try
        {
            using var output = new StringWriter();
            System.Console.SetOut(output);

            InvokePrivate(nameof(ComparisonRunner), "PrintMetric", "Metric", 100m, 90m, higher);

            var text = output.ToString();
            Assert.Contains("Metric", text);
            Assert.Contains("%", text);
        }
        finally
        {
            System.Console.SetOut(previousOut);
        }
    }

    private static void InvokePrivate(string typeName, string methodName, params object[] args)
    {
        var type = typeof(ComparisonRunner);
        Assert.Equal(typeName, type.Name);
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(null, args);
    }
}
