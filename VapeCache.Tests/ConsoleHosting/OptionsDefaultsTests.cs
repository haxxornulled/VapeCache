using VapeCache.Console.Hosting;

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
}
