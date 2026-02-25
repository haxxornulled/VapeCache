using VapeCache.Abstractions.Connections;

namespace VapeCache.Tests;

public sealed class RedisTransportProfilesTests
{
    [Fact]
    public void ApplyConnectionProfile_FullTilt_UsesAggressiveDefaults()
    {
        var options = new RedisConnectionOptions { TransportProfile = RedisTransportProfile.FullTilt };

        var effective = RedisTransportProfiles.Apply(options);

        Assert.True(effective.EnableTcpNoDelay);
        Assert.Equal(4 * 1024 * 1024, effective.TcpSendBufferBytes);
        Assert.Equal(4 * 1024 * 1024, effective.TcpReceiveBufferBytes);
    }

    [Fact]
    public void ApplyConnectionProfile_Balanced_UsesMidrangeDefaults()
    {
        var options = new RedisConnectionOptions { TransportProfile = RedisTransportProfile.Balanced };

        var effective = RedisTransportProfiles.Apply(options);

        Assert.Equal(1024 * 1024, effective.TcpSendBufferBytes);
        Assert.Equal(1024 * 1024, effective.TcpReceiveBufferBytes);
    }

    [Fact]
    public void ApplyConnectionProfile_LowLatency_UsesSmallerBuffers()
    {
        var options = new RedisConnectionOptions { TransportProfile = RedisTransportProfile.LowLatency };

        var effective = RedisTransportProfiles.Apply(options);

        Assert.Equal(256 * 1024, effective.TcpSendBufferBytes);
        Assert.Equal(256 * 1024, effective.TcpReceiveBufferBytes);
    }

    [Fact]
    public void ApplyConnectionProfile_Custom_PreservesExplicitValues()
    {
        var options = new RedisConnectionOptions
        {
            TransportProfile = RedisTransportProfile.Custom,
            EnableTcpNoDelay = false,
            TcpSendBufferBytes = 123456,
            TcpReceiveBufferBytes = 654321
        };

        var effective = RedisTransportProfiles.Apply(options);

        Assert.False(effective.EnableTcpNoDelay);
        Assert.Equal(123456, effective.TcpSendBufferBytes);
        Assert.Equal(654321, effective.TcpReceiveBufferBytes);
    }

    [Fact]
    public void ApplyMultiplexerProfile_FullTilt_UsesAggressiveBatching()
    {
        var options = new RedisMultiplexerOptions { TransportProfile = RedisTransportProfile.FullTilt };

        var effective = RedisTransportProfiles.Apply(options);

        Assert.True(effective.EnableCoalescedSocketWrites);
        Assert.False(effective.EnableSocketRespReader);
        Assert.Equal(512 * 1024, effective.CoalescedWriteMaxBytes);
        Assert.Equal(192, effective.CoalescedWriteMaxSegments);
        Assert.Equal(1536, effective.CoalescedWriteSmallCopyThresholdBytes);
        Assert.True(effective.EnableAdaptiveCoalescing);
        Assert.Equal(6, effective.AdaptiveCoalescingLowDepth);
        Assert.Equal(56, effective.AdaptiveCoalescingHighDepth);
        Assert.Equal(64 * 1024, effective.AdaptiveCoalescingMinWriteBytes);
        Assert.Equal(48, effective.AdaptiveCoalescingMinSegments);
        Assert.Equal(384, effective.AdaptiveCoalescingMinSmallCopyThresholdBytes);
    }

    [Fact]
    public void ApplyMultiplexerProfile_Balanced_UsesMidrangeBatching()
    {
        var options = new RedisMultiplexerOptions { TransportProfile = RedisTransportProfile.Balanced };

        var effective = RedisTransportProfiles.Apply(options);

        Assert.Equal(256 * 1024, effective.CoalescedWriteMaxBytes);
        Assert.Equal(128, effective.CoalescedWriteMaxSegments);
        Assert.Equal(1024, effective.CoalescedWriteSmallCopyThresholdBytes);
        Assert.True(effective.EnableAdaptiveCoalescing);
        Assert.Equal(4, effective.AdaptiveCoalescingLowDepth);
        Assert.Equal(48, effective.AdaptiveCoalescingHighDepth);
        Assert.Equal(32 * 1024, effective.AdaptiveCoalescingMinWriteBytes);
        Assert.Equal(32, effective.AdaptiveCoalescingMinSegments);
        Assert.Equal(512, effective.AdaptiveCoalescingMinSmallCopyThresholdBytes);
    }

    [Fact]
    public void ApplyMultiplexerProfile_LowLatency_UsesSmallerBatches()
    {
        var options = new RedisMultiplexerOptions { TransportProfile = RedisTransportProfile.LowLatency };

        var effective = RedisTransportProfiles.Apply(options);

        Assert.Equal(64 * 1024, effective.CoalescedWriteMaxBytes);
        Assert.Equal(64, effective.CoalescedWriteMaxSegments);
        Assert.Equal(512, effective.CoalescedWriteSmallCopyThresholdBytes);
        Assert.True(effective.EnableAdaptiveCoalescing);
        Assert.Equal(2, effective.AdaptiveCoalescingLowDepth);
        Assert.Equal(24, effective.AdaptiveCoalescingHighDepth);
        Assert.Equal(16 * 1024, effective.AdaptiveCoalescingMinWriteBytes);
        Assert.Equal(16, effective.AdaptiveCoalescingMinSegments);
        Assert.Equal(256, effective.AdaptiveCoalescingMinSmallCopyThresholdBytes);
    }

    [Fact]
    public void ApplyMultiplexerProfile_Custom_PreservesExplicitValues()
    {
        var options = new RedisMultiplexerOptions
        {
            TransportProfile = RedisTransportProfile.Custom,
            EnableCoalescedSocketWrites = false,
            EnableSocketRespReader = true,
            CoalescedWriteMaxBytes = 12345,
            CoalescedWriteMaxSegments = 42,
            CoalescedWriteSmallCopyThresholdBytes = 321,
            EnableAdaptiveCoalescing = false,
            AdaptiveCoalescingLowDepth = 5,
            AdaptiveCoalescingHighDepth = 10,
            AdaptiveCoalescingMinWriteBytes = 9000,
            AdaptiveCoalescingMinSegments = 8,
            AdaptiveCoalescingMinSmallCopyThresholdBytes = 128
        };

        var effective = RedisTransportProfiles.Apply(options);

        Assert.False(effective.EnableCoalescedSocketWrites);
        Assert.True(effective.EnableSocketRespReader);
        Assert.Equal(12345, effective.CoalescedWriteMaxBytes);
        Assert.Equal(42, effective.CoalescedWriteMaxSegments);
        Assert.Equal(321, effective.CoalescedWriteSmallCopyThresholdBytes);
        Assert.False(effective.EnableAdaptiveCoalescing);
        Assert.Equal(5, effective.AdaptiveCoalescingLowDepth);
        Assert.Equal(10, effective.AdaptiveCoalescingHighDepth);
        Assert.Equal(9000, effective.AdaptiveCoalescingMinWriteBytes);
        Assert.Equal(8, effective.AdaptiveCoalescingMinSegments);
        Assert.Equal(128, effective.AdaptiveCoalescingMinSmallCopyThresholdBytes);
    }
}
