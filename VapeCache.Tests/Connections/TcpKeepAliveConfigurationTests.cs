using System.Net.Sockets;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Tests.Connections;

/// <summary>
/// Tests for TCP keep-alive socket configuration.
/// These tests verify that keep-alive settings are properly applied to the underlying socket.
/// </summary>
public sealed class TcpKeepAliveConfigurationTests
{
    [SkippableFact]
    public void TcpKeepAlive_IsAppliedToSocket_WhenEnabled()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "TCP keep-alive configuration is Windows-only");

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

        var options = new RedisConnectionOptions
        {
            Host = "localhost",
            Port = 6379,
            EnableTcpKeepAlive = true,
            TcpKeepAliveTime = TimeSpan.FromSeconds(60),
            TcpKeepAliveInterval = TimeSpan.FromSeconds(15)
        };

        // Call the private method using reflection for testing
        var method = typeof(VapeCache.Infrastructure.Connections.RedisConnectionFactory)
            .GetMethod("TryConfigureKeepAlive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        method.Invoke(null, new object[] { socket, options });

        // Verify KeepAlive is enabled
        var keepAlive = socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive);
        Assert.NotNull(keepAlive);
        Assert.True(keepAlive is int value && value != 0, "KeepAlive should be enabled on socket");
    }

    [SkippableFact]
    public void TcpKeepAlive_IsNotApplied_WhenDisabled()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "TCP keep-alive configuration is Windows-only");

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

        var options = new RedisConnectionOptions
        {
            Host = "localhost",
            Port = 6379,
            EnableTcpKeepAlive = false,
            TcpKeepAliveTime = TimeSpan.FromSeconds(60),
            TcpKeepAliveInterval = TimeSpan.FromSeconds(15)
        };

        // Call the private method using reflection for testing
        var method = typeof(VapeCache.Infrastructure.Connections.RedisConnectionFactory)
            .GetMethod("TryConfigureKeepAlive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        method.Invoke(null, new object[] { socket, options });

        // Verify KeepAlive is NOT enabled (should be default/false)
        var keepAlive = socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive);
        // Default socket has KeepAlive = 0 (disabled)
        Assert.True(keepAlive is int value && value == 0, "KeepAlive should be disabled on socket");
    }

    [SkippableFact]
    public void TcpKeepAlive_DoesNotThrow_OnNonWindows()
    {
        Skip.If(OperatingSystem.IsWindows(), "This test validates non-Windows behavior");

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

        var options = new RedisConnectionOptions
        {
            Host = "localhost",
            Port = 6379,
            EnableTcpKeepAlive = true,
            TcpKeepAliveTime = TimeSpan.FromSeconds(60),
            TcpKeepAliveInterval = TimeSpan.FromSeconds(15)
        };

        // Call the private method using reflection for testing
        var method = typeof(VapeCache.Infrastructure.Connections.RedisConnectionFactory)
            .GetMethod("TryConfigureKeepAlive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        // Should not throw on Linux/macOS (method returns early)
        var exception = Record.Exception(() => method.Invoke(null, new object[] { socket, options }));
        Assert.Null(exception);
    }

    [SkippableFact]
    public void TcpKeepAlive_ClampsValues_ToValidRange()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "TCP keep-alive configuration is Windows-only");

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

        // Test with values that would overflow if not clamped
        var options = new RedisConnectionOptions
        {
            Host = "localhost",
            Port = 6379,
            EnableTcpKeepAlive = true,
            TcpKeepAliveTime = TimeSpan.FromDays(100),  // Very large value
            TcpKeepAliveInterval = TimeSpan.FromDays(10) // Very large value
        };

        // Call the private method using reflection for testing
        var method = typeof(VapeCache.Infrastructure.Connections.RedisConnectionFactory)
            .GetMethod("TryConfigureKeepAlive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        // Should not throw even with extreme values (should clamp to int.MaxValue)
        var exception = Record.Exception(() => method.Invoke(null, new object[] { socket, options }));
        Assert.Null(exception);
    }

    [Fact]
    public void TcpKeepAlive_DefaultValues_AreReasonable()
    {
        var options = new RedisConnectionOptions
        {
            Host = "localhost",
            Port = 6379
        };

        // Verify defaults from RedisConnectionOptions
        Assert.True(options.EnableTcpNoDelay, "TCP no-delay should be enabled by default");
        Assert.Equal(4 * 1024 * 1024, options.TcpSendBufferBytes);
        Assert.Equal(4 * 1024 * 1024, options.TcpReceiveBufferBytes);
        Assert.True(options.EnableTcpKeepAlive, "TCP keep-alive should be enabled by default");
        Assert.Equal(TimeSpan.FromSeconds(30), options.TcpKeepAliveTime);
        Assert.Equal(TimeSpan.FromSeconds(10), options.TcpKeepAliveInterval);
    }

    [Fact]
    public void TcpTransportOptions_ApplyNoDelaySetting()
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

        var options = new RedisConnectionOptions
        {
            Host = "localhost",
            Port = 6379,
            EnableTcpNoDelay = false
        };

        var method = typeof(VapeCache.Infrastructure.Connections.RedisConnectionFactory)
            .GetMethod("TryConfigureSocketTransport", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        method.Invoke(null, new object[] { socket, options });

        Assert.False(socket.NoDelay, "NoDelay should be disabled when EnableTcpNoDelay=false");
    }

    [Theory]
    [InlineData(0, 10)]   // Zero time
    [InlineData(30, 0)]   // Zero interval
    [InlineData(-1, 10)]  // Negative time
    [InlineData(30, -1)]  // Negative interval
    public void TcpKeepAlive_HandlesInvalidValues_Gracefully(int timeSeconds, int intervalSeconds)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

        var options = new RedisConnectionOptions
        {
            Host = "localhost",
            Port = 6379,
            EnableTcpKeepAlive = true,
            TcpKeepAliveTime = TimeSpan.FromSeconds(timeSeconds),
            TcpKeepAliveInterval = TimeSpan.FromSeconds(intervalSeconds)
        };

        // Call the private method using reflection for testing
        var method = typeof(VapeCache.Infrastructure.Connections.RedisConnectionFactory)
            .GetMethod("TryConfigureKeepAlive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        // Should not throw - will clamp to minimum of 1ms
        var exception = Record.Exception(() => method.Invoke(null, new object[] { socket, options }));

        // Exception might occur on Windows if values are truly invalid, but should be caught
        // The method has try-catch and should never throw
        Assert.Null(exception);
    }
}
