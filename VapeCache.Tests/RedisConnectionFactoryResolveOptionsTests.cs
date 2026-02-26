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

    [Fact]
    public void ResolveOptions_PreservesCustomKeepAliveValues_OnNonLoopback()
    {
        var o = new RedisConnectionOptions
        {
            Host = "redis.example.com",
            EnableTcpKeepAlive = true,
            TcpKeepAliveTime = TimeSpan.FromMinutes(5),
            TcpKeepAliveInterval = TimeSpan.FromSeconds(5)
        };

        var effective = RedisConnectionFactory.ResolveOptions(o);

        Assert.True(effective.EnableTcpKeepAlive);
        Assert.Equal(TimeSpan.FromMinutes(5), effective.TcpKeepAliveTime);
        Assert.Equal(TimeSpan.FromSeconds(5), effective.TcpKeepAliveInterval);
    }

    [Fact]
    public void ResolveOptions_PreservesCustomKeepAliveValues_OnLoopback()
    {
        // Custom (non-default) values should NOT be disabled on loopback
        var o = new RedisConnectionOptions
        {
            Host = "localhost",
            EnableTcpKeepAlive = true,
            TcpKeepAliveTime = TimeSpan.FromMinutes(2),  // custom
            TcpKeepAliveInterval = TimeSpan.FromSeconds(5) // custom
        };

        var effective = RedisConnectionFactory.ResolveOptions(o);

        // Should NOT disable keep-alive because values are custom (not defaults)
        Assert.True(effective.EnableTcpKeepAlive);
        Assert.Equal(TimeSpan.FromMinutes(2), effective.TcpKeepAliveTime);
        Assert.Equal(TimeSpan.FromSeconds(5), effective.TcpKeepAliveInterval);
    }

    [Fact]
    public void ResolveOptions_DisablesKeepAlive_WhenExplicitlyDisabled()
    {
        var o = new RedisConnectionOptions
        {
            Host = "redis.example.com",
            EnableTcpKeepAlive = false,
            TcpKeepAliveTime = TimeSpan.FromSeconds(30),
            TcpKeepAliveInterval = TimeSpan.FromSeconds(10)
        };

        var effective = RedisConnectionFactory.ResolveOptions(o);

        Assert.False(effective.EnableTcpKeepAlive);
    }

    [Theory]
    [InlineData(1)]      // 1ms minimum
    [InlineData(1000)]   // 1 second
    [InlineData(30000)]  // 30 seconds (default)
    [InlineData(120000)] // 2 minutes
    [InlineData(int.MaxValue)] // Max value
    public void ResolveOptions_AcceptsValidKeepAliveTime(int milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds);
        var o = new RedisConnectionOptions
        {
            Host = "redis.example.com",
            EnableTcpKeepAlive = true,
            TcpKeepAliveTime = time,
            TcpKeepAliveInterval = TimeSpan.FromSeconds(10)
        };

        var effective = RedisConnectionFactory.ResolveOptions(o);

        Assert.Equal(time, effective.TcpKeepAliveTime);
    }

    [Theory]
    [InlineData(1)]      // 1ms minimum
    [InlineData(1000)]   // 1 second
    [InlineData(10000)]  // 10 seconds (default)
    [InlineData(30000)]  // 30 seconds
    [InlineData(int.MaxValue)] // Max value
    public void ResolveOptions_AcceptsValidKeepAliveInterval(int milliseconds)
    {
        var interval = TimeSpan.FromMilliseconds(milliseconds);
        var o = new RedisConnectionOptions
        {
            Host = "redis.example.com",
            EnableTcpKeepAlive = true,
            TcpKeepAliveTime = TimeSpan.FromSeconds(30),
            TcpKeepAliveInterval = interval
        };

        var effective = RedisConnectionFactory.ResolveOptions(o);

        Assert.Equal(interval, effective.TcpKeepAliveInterval);
    }

    [Fact]
    public void ResolveOptions_PreservesTcpTransportTuningValues()
    {
        var o = new RedisConnectionOptions
        {
            Host = "redis.example.com",
            EnableTcpNoDelay = false,
            TcpSendBufferBytes = 256 * 1024,
            TcpReceiveBufferBytes = 512 * 1024
        };

        var effective = RedisConnectionFactory.ResolveOptions(o);

        Assert.False(effective.EnableTcpNoDelay);
        Assert.Equal(256 * 1024, effective.TcpSendBufferBytes);
        Assert.Equal(512 * 1024, effective.TcpReceiveBufferBytes);
    }
}
