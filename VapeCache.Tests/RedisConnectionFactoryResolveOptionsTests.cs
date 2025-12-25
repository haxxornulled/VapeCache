using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests;

public sealed class RedisConnectionFactoryResolveOptionsTests
{
    [Fact]
    public void ResolveOptions_DisablesBorrowPingValidation_ForNonLoopback_WhenUsingDefault()
    {
        var o = new RedisConnectionOptions
        {
            Host = "10.0.0.5",
            ValidateAfterIdle = TimeSpan.FromSeconds(30), // default
            EnableTcpKeepAlive = true
        };

        var effective = RedisConnectionFactory.ResolveOptions(o);

        Assert.Equal(TimeSpan.Zero, effective.ValidateAfterIdle);
        Assert.True(effective.EnableTcpKeepAlive);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void ResolveOptions_DisablesKeepAlive_OnLoopback_WhenUsingDefaultKeepAlive(string host)
    {
        var o = new RedisConnectionOptions
        {
            Host = host,
            ValidateAfterIdle = TimeSpan.FromSeconds(30), // default
            EnableTcpKeepAlive = true,
            TcpKeepAliveTime = TimeSpan.FromSeconds(30),
            TcpKeepAliveInterval = TimeSpan.FromSeconds(10)
        };

        var effective = RedisConnectionFactory.ResolveOptions(o);

        Assert.Equal(TimeSpan.Zero, effective.ValidateAfterIdle);
        Assert.False(effective.EnableTcpKeepAlive);
    }

    [Fact]
    public void ResolveOptions_DoesNotOverrideExplicitValidateAfterIdle()
    {
        var o = new RedisConnectionOptions
        {
            Host = "10.0.0.5",
            ValidateAfterIdle = TimeSpan.FromMinutes(2) // explicit
        };

        var effective = RedisConnectionFactory.ResolveOptions(o);

        Assert.Equal(TimeSpan.FromMinutes(2), effective.ValidateAfterIdle);
    }
}
