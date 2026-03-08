using VapeCache.Benchmarks;

namespace VapeCache.Tests.Benchmarks;

public class ComparisonReportTests
{
    [Theory]
    [InlineData("StackExchange", 1)]
    [InlineData("SER_StringSetGet", 1)]
    [InlineData("VapeCache", 2)]
    [InlineData("Ours_StringSetGet", 2)]
    [InlineData("UnknownMethod", 0)]
    public void DetectClient_ReturnsExpectedClient(string methodName, int expectedClient)
    {
        var actual = ComparisonReport.DetectClient(methodName);
        Assert.Equal((ComparisonClient)expectedClient, actual);
    }

    [Fact]
    public void BuildRows_PairsSamplesAndComputesWinner()
    {
        var samples = new[]
        {
            new ComparisonSample("SuiteA", "StringSetGet", "PayloadBytes=256", ComparisonClient.StackExchangeRedis, 100, 1024),
            new ComparisonSample("SuiteA", "StringSetGet", "PayloadBytes=256", ComparisonClient.VapeCache, 80, 2300)
        };

        var row = Assert.Single(ComparisonReport.BuildRows(samples));
        Assert.Equal("VapeCache", row.Winner);
        Assert.Equal(0.8, row.RatioVapeToStackExchange, 3);
        Assert.Equal(-20.0, row.DeltaPercent, 1);
    }

    [Fact]
    public void BuildRows_UsesTieThreshold()
    {
        var samples = new[]
        {
            new ComparisonSample("SuiteA", "Ping", "-", ComparisonClient.StackExchangeRedis, 100, 500),
            new ComparisonSample("SuiteA", "Ping", "-", ComparisonClient.VapeCache, 100.3, 700)
        };

        var row = Assert.Single(ComparisonReport.BuildRows(samples));
        Assert.Equal("Tie", row.Winner);
    }

    [Fact]
    public void BuildMarkdown_ContainsSummaryAndResults()
    {
        var samples = new[]
        {
            new ComparisonSample("SuiteA", "StringSetGet", "PayloadBytes=256", ComparisonClient.StackExchangeRedis, 100, 1024),
            new ComparisonSample("SuiteA", "StringSetGet", "PayloadBytes=256", ComparisonClient.VapeCache, 80, 2300),
            new ComparisonSample("SuiteA", "Ping", "-", ComparisonClient.StackExchangeRedis, 90, 520),
            new ComparisonSample("SuiteA", "Ping", "-", ComparisonClient.VapeCache, 95, 680)
        };

        var markdown = ComparisonReport.BuildMarkdown(samples);

        Assert.Contains("# Redis Head-to-Head Comparison", markdown, StringComparison.Ordinal);
        Assert.Contains("- Comparisons: 2", markdown, StringComparison.Ordinal);
        Assert.Contains("- VapeCache faster: 1", markdown, StringComparison.Ordinal);
        Assert.Contains("- StackExchange.Redis faster: 1", markdown, StringComparison.Ordinal);
        Assert.Contains("|SuiteA|StringSetGet|PayloadBytes=256|100.00|80.00|0.800|-20.0%|VapeCache|1024|2300|", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMarkdown_EmitsConfiguredReportingAudience()
    {
        const string varName = "VAPECACHE_BENCH_REPORT_AUDIENCE";
        var prior = Environment.GetEnvironmentVariable(varName);

        try
        {
            Environment.SetEnvironmentVariable(varName, "hot-path comparison");
            var markdown = ComparisonReport.BuildMarkdown(Array.Empty<ComparisonSample>());
            Assert.Contains("Reporting audience: hot-path comparison", markdown, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, prior);
        }
    }
}
