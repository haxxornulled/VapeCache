using VapeCache.Benchmarks;

namespace VapeCache.Tests.Benchmarks;

public sealed class BenchmarkCommandLineTests
{
    [Fact]
    public void BuildEffectiveArgs_DefaultsToMicroCategory_WhenNoExplicitSelectionIsProvided()
    {
        var args = BenchmarkCommandLine.BuildEffectiveArgs([]);

        Assert.Equal(["--anyCategories", "Micro"], args);
    }

    [Fact]
    public void BuildEffectiveArgs_DoesNotOverrideExplicitFilter()
    {
        var args = BenchmarkCommandLine.BuildEffectiveArgs(["--filter", "*RedisBackendRoundTripBenchmarks*"]);

        Assert.Equal(["--filter", "*RedisBackendRoundTripBenchmarks*"], args);
    }

    [Fact]
    public void BuildEffectiveArgs_StripsIncludeLiveFlag_AndSkipsDefaultMicroFilter()
    {
        var args = BenchmarkCommandLine.BuildEffectiveArgs(["--include-live"]);

        Assert.Empty(args);
    }
}
