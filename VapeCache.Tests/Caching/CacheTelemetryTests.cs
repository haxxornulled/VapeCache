using VapeCache.Infrastructure.Caching;

namespace VapeCache.Tests.Caching;

public sealed class CacheTelemetryTests
{
    [Fact]
    public void GetCurrentBackendMeasurement_ReturnsUnknown_WhenNotInitialized()
    {
        CacheTelemetry.ResetForTesting();

        var measurement = CacheTelemetry.GetCurrentBackendMeasurement();

        Assert.Equal(-1, measurement.Value);
    }

    [Theory]
    [InlineData("redis", 1)]
    [InlineData("memory", 0)]
    [InlineData("in-memory", 0)]
    [InlineData("unknown", -1)]
    public void MapBackendName_ReturnsExpectedValue(string backend, int expected)
    {
        Assert.Equal(expected, CacheTelemetry.MapBackendName(backend));
    }
}
