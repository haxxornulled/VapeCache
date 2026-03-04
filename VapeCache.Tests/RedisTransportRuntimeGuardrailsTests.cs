using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests;

public sealed class RedisTransportRuntimeGuardrailsTests
{
    [Fact]
    public void ApplyRuntimeConnection_NormalizesInvalidValues()
    {
        var options = new RedisConnectionOptions
        {
            TransportProfile = RedisTransportProfile.Custom,
            Host = "",
            Port = 0,
            MaxConnections = -5,
            MaxIdle = -3,
            Warm = 999,
            ConnectTimeout = TimeSpan.Zero,
            AcquireTimeout = TimeSpan.Zero,
            ValidateAfterIdle = TimeSpan.FromSeconds(-1),
            ValidateTimeout = TimeSpan.FromSeconds(-1),
            IdleTimeout = TimeSpan.FromSeconds(-1),
            MaxConnectionLifetime = TimeSpan.FromSeconds(-1),
            ReaperPeriod = TimeSpan.FromSeconds(-1),
            TcpSendBufferBytes = 1,
            TcpReceiveBufferBytes = 99_999_999,
            TcpKeepAliveTime = TimeSpan.Zero,
            TcpKeepAliveInterval = TimeSpan.Zero,
            MaxBulkStringBytes = 0,
            MaxArrayDepth = 0,
            RespProtocolVersion = 0,
            MaxClusterRedirects = -5
        };

        var effective = RedisRuntimeOptionsNormalizer.NormalizeConnection(options);

        Assert.Equal("localhost", effective.Host);
        Assert.Equal(6379, effective.Port);
        Assert.Equal(1, effective.MaxConnections);
        Assert.Equal(1, effective.MaxIdle);
        Assert.Equal(1, effective.Warm);
        Assert.Equal(TimeSpan.FromSeconds(2), effective.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), effective.AcquireTimeout);
        Assert.Equal(TimeSpan.Zero, effective.ValidateAfterIdle);
        Assert.Equal(TimeSpan.Zero, effective.ValidateTimeout);
        Assert.Equal(TimeSpan.Zero, effective.IdleTimeout);
        Assert.Equal(TimeSpan.Zero, effective.MaxConnectionLifetime);
        Assert.Equal(TimeSpan.Zero, effective.ReaperPeriod);
        Assert.Equal(4 * 1024, effective.TcpSendBufferBytes);
        Assert.Equal(4 * 1024 * 1024, effective.TcpReceiveBufferBytes);
        Assert.Equal(TimeSpan.FromSeconds(30), effective.TcpKeepAliveTime);
        Assert.Equal(TimeSpan.FromSeconds(10), effective.TcpKeepAliveInterval);
        Assert.Equal(16 * 1024 * 1024, effective.MaxBulkStringBytes);
        Assert.Equal(64, effective.MaxArrayDepth);
        Assert.Equal(2, effective.RespProtocolVersion);
        Assert.Equal(0, effective.MaxClusterRedirects);
    }

    [Fact]
    public void ApplyRuntimeConnection_ClampsUpperBoundsForRespAndRedirects()
    {
        var options = new RedisConnectionOptions
        {
            TransportProfile = RedisTransportProfile.Custom,
            RespProtocolVersion = 99,
            MaxClusterRedirects = 999
        };

        var effective = RedisRuntimeOptionsNormalizer.NormalizeConnection(options);

        Assert.Equal(3, effective.RespProtocolVersion);
        Assert.Equal(16, effective.MaxClusterRedirects);
    }

    [Fact]
    public void ApplyRuntimeMultiplexer_NormalizesInvalidValues()
    {
        var options = new RedisMultiplexerOptions
        {
            TransportProfile = RedisTransportProfile.Custom,
            UseDedicatedLaneWorkers = true,
            EnableAutoscaling = true,
            Connections = 0,
            MaxInFlightPerConnection = 0,
            CoalescedWriteMaxBytes = 1,
            CoalescedWriteMaxSegments = 1,
            CoalescedWriteSmallCopyThresholdBytes = 1,
            AdaptiveCoalescingLowDepth = 0,
            AdaptiveCoalescingHighDepth = 0,
            AdaptiveCoalescingMinWriteBytes = 1,
            AdaptiveCoalescingMinSegments = 0,
            AdaptiveCoalescingMinSmallCopyThresholdBytes = 0,
            ResponseTimeout = TimeSpan.FromMilliseconds(-1),
            BulkLaneConnections = -5,
            BulkLaneResponseTimeout = TimeSpan.Zero,
            MinConnections = 4,
            MaxConnections = 2,
            AutoscaleSampleInterval = TimeSpan.Zero,
            ScaleUpWindow = TimeSpan.Zero,
            ScaleDownWindow = TimeSpan.Zero,
            ScaleUpCooldown = TimeSpan.Zero,
            ScaleDownCooldown = TimeSpan.Zero,
            ScaleUpInflightUtilization = 50,
            ScaleDownInflightUtilization = -5,
            ScaleUpQueueDepthThreshold = 0,
            ScaleUpTimeoutRatePerSecThreshold = -2,
            ScaleUpP99LatencyMsThreshold = 0,
            ScaleDownP95LatencyMsThreshold = 0,
            EmergencyScaleUpTimeoutRatePerSecThreshold = 0,
            ScaleDownDrainTimeout = TimeSpan.Zero,
            MaxScaleEventsPerMinute = 0,
            FlapToggleThreshold = 0,
            AutoscaleFreezeDuration = TimeSpan.Zero,
            ReconnectStormFailureRatePerSecThreshold = 0
        };

        var effective = RedisRuntimeOptionsNormalizer.NormalizeMultiplexer(options);

        Assert.Equal(4, effective.Connections);
        Assert.True(effective.UseDedicatedLaneWorkers);
        Assert.Equal(64, effective.MaxInFlightPerConnection);
        Assert.Equal(16 * 1024, effective.CoalescedWriteMaxBytes);
        Assert.Equal(16, effective.CoalescedWriteMaxSegments);
        Assert.Equal(64, effective.CoalescedWriteSmallCopyThresholdBytes);
        Assert.Equal(1, effective.AdaptiveCoalescingLowDepth);
        Assert.Equal(1, effective.AdaptiveCoalescingHighDepth);
        Assert.Equal(4 * 1024, effective.AdaptiveCoalescingMinWriteBytes);
        Assert.Equal(1, effective.AdaptiveCoalescingMinSegments);
        Assert.Equal(64, effective.AdaptiveCoalescingMinSmallCopyThresholdBytes);
        Assert.Equal(TimeSpan.Zero, effective.ResponseTimeout);
        Assert.Equal(1, effective.BulkLaneConnections);
        Assert.Equal(TimeSpan.FromSeconds(5), effective.BulkLaneResponseTimeout);
        Assert.Equal(4, effective.MinConnections);
        Assert.Equal(4, effective.MaxConnections);
        Assert.Equal(TimeSpan.FromSeconds(1), effective.AutoscaleSampleInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), effective.ScaleUpWindow);
        Assert.Equal(TimeSpan.FromMinutes(2), effective.ScaleDownWindow);
        Assert.Equal(TimeSpan.FromSeconds(20), effective.ScaleUpCooldown);
        Assert.Equal(TimeSpan.FromSeconds(90), effective.ScaleDownCooldown);
        Assert.Equal(0.98, effective.ScaleUpInflightUtilization);
        Assert.Equal(0.01, effective.ScaleDownInflightUtilization);
        Assert.Equal(1, effective.ScaleUpQueueDepthThreshold);
        Assert.Equal(0.01, effective.ScaleUpTimeoutRatePerSecThreshold);
        Assert.Equal(1.0, effective.ScaleUpP99LatencyMsThreshold);
        Assert.Equal(0.5, effective.ScaleDownP95LatencyMsThreshold);
        Assert.Equal(0.01, effective.EmergencyScaleUpTimeoutRatePerSecThreshold);
        Assert.Equal(TimeSpan.FromSeconds(5), effective.ScaleDownDrainTimeout);
        Assert.Equal(1, effective.MaxScaleEventsPerMinute);
        Assert.Equal(2, effective.FlapToggleThreshold);
        Assert.Equal(TimeSpan.FromMinutes(2), effective.AutoscaleFreezeDuration);
        Assert.Equal(0.01, effective.ReconnectStormFailureRatePerSecThreshold);
    }

    [Fact]
    public void ApplyRuntimeMultiplexer_PreservesValidCustomValues()
    {
        var options = new RedisMultiplexerOptions
        {
            TransportProfile = RedisTransportProfile.Custom,
            Connections = 6,
            MaxInFlightPerConnection = 6000,
            CoalescedWriteMaxBytes = 256 * 1024,
            CoalescedWriteMaxSegments = 200,
            CoalescedWriteSmallCopyThresholdBytes = 1024,
            AdaptiveCoalescingLowDepth = 8,
            AdaptiveCoalescingHighDepth = 64,
            AdaptiveCoalescingMinWriteBytes = 32 * 1024,
            AdaptiveCoalescingMinSegments = 48,
            AdaptiveCoalescingMinSmallCopyThresholdBytes = 512,
            BulkLaneConnections = 2,
            BulkLaneResponseTimeout = TimeSpan.FromSeconds(9)
        };

        var effective = RedisRuntimeOptionsNormalizer.NormalizeMultiplexer(options);

        Assert.Equal(options, effective);
    }
}
