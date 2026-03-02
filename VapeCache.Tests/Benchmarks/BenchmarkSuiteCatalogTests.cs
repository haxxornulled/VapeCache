using VapeCache.Benchmarks;

namespace VapeCache.Tests.Benchmarks;

public sealed class BenchmarkSuiteCatalogTests
{
    [Fact]
    public void TryCreateInvocationPlan_BuildsFeatureSetPlan()
    {
        var created = BenchmarkSuiteCatalog.TryCreateInvocationPlan(
            ["featuresets", "all", "--job", "Short"],
            out var plan,
            out var error);

        Assert.True(created, error);
        Assert.Equal("Feature sets (all)", plan.DisplayName);
        Assert.Equal("--filter", plan.Arguments[0]);
        Assert.Contains("*CacheServiceApiBenchmarks*", plan.Arguments);
        Assert.Contains("*CircuitBreakerPerformanceBenchmarks*", plan.Arguments);
        Assert.Contains("*RedisRespProtocolBenchmarks*", plan.Arguments);
        Assert.Contains("*RedisMultiplexedConnectionBenchmarks*", plan.Arguments);
        Assert.Contains("*SanityBenchmarks*", plan.Arguments);
        Assert.Contains("--job", plan.Arguments);
        Assert.Contains("Short", plan.Arguments);
        Assert.Empty(plan.EnvironmentDefaults);
    }

    [Fact]
    public void TryCreateInvocationPlan_BuildsComparisonPlanWithPayloadDefaults()
    {
        var created = BenchmarkSuiteCatalog.TryCreateInvocationPlan(
            ["compare", "client"],
            out var plan,
            out var error);

        Assert.True(created, error);
        Assert.Equal("Comparison: client", plan.DisplayName);
        Assert.Equal(["--filter", "*RedisClientHeadToHeadBenchmarks*"], plan.Arguments);
        var payloadDefault = Assert.Single(plan.EnvironmentDefaults);
        Assert.Equal("VAPECACHE_BENCH_CLIENT_PAYLOADS", payloadDefault.Key);
        Assert.Equal("256,1024,4096,16384", payloadDefault.Value);
    }

    [Fact]
    public void TryCreateInvocationPlan_BuildsDatatypeParityPlanWithMuxDefaults()
    {
        var created = BenchmarkSuiteCatalog.TryCreateInvocationPlan(
            ["compare", "datatypes"],
            out var plan,
            out var error);

        Assert.True(created, error);
        Assert.Equal("Comparison: datatypes", plan.DisplayName);
        Assert.Equal(["--filter", "*RedisDatatypeParityHeadToHeadBenchmarks*"], plan.Arguments);
        Assert.Equal(3, plan.EnvironmentDefaults.Length);
        Assert.Contains(plan.EnvironmentDefaults, static entry => entry.Key == "VAPECACHE_BENCH_DATATYPE_PAYLOADS" && entry.Value == "256,1024,4096,16384");
        Assert.Contains(plan.EnvironmentDefaults, static entry => entry.Key == "VAPECACHE_BENCH_DEDICATED_LANE_WORKERS" && entry.Value == "true");
        Assert.Contains(plan.EnvironmentDefaults, static entry => entry.Key == "VAPECACHE_BENCH_SOCKET_RESP_READER" && entry.Value == "true");
    }

    [Fact]
    public void TryCreateInvocationPlan_RejectsUnknownSuite()
    {
        var created = BenchmarkSuiteCatalog.TryCreateInvocationPlan(
            ["compare", "missing"],
            out _,
            out var error);

        Assert.False(created);
        Assert.Contains("Unknown Comparisons suite 'missing'.", error, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatCatalog_FiltersToRequestedAudience()
    {
        var catalog = BenchmarkSuiteCatalog.FormatCatalog("compare");

        Assert.Contains("Comparisons:", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("Feature sets:", catalog, StringComparison.Ordinal);
        Assert.Contains("client", catalog, StringComparison.Ordinal);
        Assert.Contains("modules", catalog, StringComparison.Ordinal);
        Assert.Contains("datatypes", catalog, StringComparison.Ordinal);
    }
}
