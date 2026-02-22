using VapeCache.Console.Stress;

namespace VapeCache.Tests.ConsoleStress;

public sealed class RedisStressOptionsTests
{
    [Fact]
    public void Defaults_are_sane()
    {
        var o = new RedisStressOptions();

        Assert.True(o.Enabled);
        Assert.Equal("pool", o.Mode);
        Assert.Equal("ping", o.Workload);
        Assert.True(o.Workers > 0);
        Assert.True(o.BurstRequests > 0);
        Assert.True(o.OperationTimeout > TimeSpan.Zero);
    }

    [Fact]
    public void Supports_custom_values()
    {
        var o = new RedisStressOptions
        {
            Enabled = false,
            Mode = "mux",
            Workload = "payload",
            Workers = 4,
            TargetRps = 1234,
            BurnConnectionsTarget = 42
        };

        Assert.False(o.Enabled);
        Assert.Equal("mux", o.Mode);
        Assert.Equal("payload", o.Workload);
        Assert.Equal(4, o.Workers);
        Assert.Equal(1234, o.TargetRps);
        Assert.Equal(42, o.BurnConnectionsTarget);
    }
}
