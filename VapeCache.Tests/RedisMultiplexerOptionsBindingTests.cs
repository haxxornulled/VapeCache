using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Text;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Tests;

public sealed class RedisMultiplexerOptionsBindingTests
{
    [Fact]
    public void Defaults_are_full_tilt_profile()
    {
        var o = new RedisMultiplexerOptions();

        Assert.Equal(RedisTransportProfile.FullTilt, o.TransportProfile);
        Assert.False(o.EnableCommandInstrumentation);
        Assert.False(o.EnableSocketRespReader);
        Assert.False(o.UseDedicatedLaneWorkers);
        Assert.Equal(512 * 1024, o.CoalescedWriteMaxBytes);
        Assert.Equal(192, o.CoalescedWriteMaxSegments);
        Assert.Equal(1536, o.CoalescedWriteSmallCopyThresholdBytes);
        Assert.True(o.EnableAdaptiveCoalescing);
        Assert.Equal(6, o.AdaptiveCoalescingLowDepth);
        Assert.Equal(56, o.AdaptiveCoalescingHighDepth);
        Assert.Equal(64 * 1024, o.AdaptiveCoalescingMinWriteBytes);
        Assert.Equal(48, o.AdaptiveCoalescingMinSegments);
        Assert.Equal(384, o.AdaptiveCoalescingMinSmallCopyThresholdBytes);
        Assert.Equal(8, o.CoalescingEnterQueueDepth);
        Assert.Equal(3, o.CoalescingExitQueueDepth);
        Assert.Equal(128, o.CoalescedWriteMaxOperations);
        Assert.Equal(8, o.CoalescingSpinBudget);
        Assert.False(o.EnableAutoscaling);
        Assert.True(o.Connections >= 2);
        Assert.Equal(1, o.BulkLaneConnections);
        Assert.False(o.AutoAdjustBulkLanes);
        Assert.Equal(0.25, o.BulkLaneTargetRatio);
        Assert.Equal(TimeSpan.FromSeconds(5), o.BulkLaneResponseTimeout);
        Assert.Equal(0, o.PubSubLaneConnections);
        Assert.Equal(0, o.BlockingLaneConnections);
        Assert.Equal(2, o.MaxScaleEventsPerMinute);
        Assert.Equal(4, o.FlapToggleThreshold);
        Assert.Equal(TimeSpan.FromMinutes(2), o.AutoscaleFreezeDuration);
        Assert.Equal(2.0, o.ReconnectStormFailureRatePerSecThreshold);
    }

    [Fact]
    public void Options_bind_from_configuration()
    {
        var json = """
        {
          "RedisMultiplexer": {
            "Connections": 8,
            "MaxInFlightPerConnection": 8192,
            "TransportProfile": "LowLatency",
            "EnableCommandInstrumentation": true,
            "EnableCoalescedSocketWrites": true,
            "EnableSocketRespReader": true,
            "UseDedicatedLaneWorkers": true,
            "CoalescedWriteMaxBytes": 131072,
            "CoalescedWriteMaxSegments": 64,
            "CoalescedWriteSmallCopyThresholdBytes": 1024,
            "EnableAdaptiveCoalescing": true,
            "AdaptiveCoalescingLowDepth": 2,
            "AdaptiveCoalescingHighDepth": 24,
            "AdaptiveCoalescingMinWriteBytes": 16384,
            "AdaptiveCoalescingMinSegments": 16,
            "AdaptiveCoalescingMinSmallCopyThresholdBytes": 256,
            "CoalescingEnterQueueDepth": 12,
            "CoalescingExitQueueDepth": 5,
            "CoalescedWriteMaxOperations": 96,
            "CoalescingSpinBudget": 10,
            "ResponseTimeout": "00:00:01.500",
            "BulkLaneConnections": 2,
            "AutoAdjustBulkLanes": true,
            "BulkLaneTargetRatio": 0.3,
            "BulkLaneResponseTimeout": "00:00:06",
            "PubSubLaneConnections": 0,
            "BlockingLaneConnections": 0,
            "MaxScaleEventsPerMinute": 3,
            "FlapToggleThreshold": 5,
            "AutoscaleFreezeDuration": "00:03:00",
            "ReconnectStormFailureRatePerSecThreshold": 3.5
          }
        }
        """;

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var services = new ServiceCollection();
        services
            .AddOptions<RedisMultiplexerOptions>()
            .Bind(config.GetSection("RedisMultiplexer"));

        using var sp = services.BuildServiceProvider();
        var o = sp.GetRequiredService<IOptions<RedisMultiplexerOptions>>().Value;

        Assert.Equal(8, o.Connections);
        Assert.Equal(8192, o.MaxInFlightPerConnection);
        Assert.Equal(RedisTransportProfile.LowLatency, o.TransportProfile);
        Assert.True(o.EnableCommandInstrumentation);
        Assert.True(o.EnableCoalescedSocketWrites);
        Assert.True(o.EnableSocketRespReader);
        Assert.True(o.UseDedicatedLaneWorkers);
        Assert.Equal(131072, o.CoalescedWriteMaxBytes);
        Assert.Equal(64, o.CoalescedWriteMaxSegments);
        Assert.Equal(1024, o.CoalescedWriteSmallCopyThresholdBytes);
        Assert.True(o.EnableAdaptiveCoalescing);
        Assert.Equal(2, o.AdaptiveCoalescingLowDepth);
        Assert.Equal(24, o.AdaptiveCoalescingHighDepth);
        Assert.Equal(16384, o.AdaptiveCoalescingMinWriteBytes);
        Assert.Equal(16, o.AdaptiveCoalescingMinSegments);
        Assert.Equal(256, o.AdaptiveCoalescingMinSmallCopyThresholdBytes);
        Assert.Equal(12, o.CoalescingEnterQueueDepth);
        Assert.Equal(5, o.CoalescingExitQueueDepth);
        Assert.Equal(96, o.CoalescedWriteMaxOperations);
        Assert.Equal(10, o.CoalescingSpinBudget);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), o.ResponseTimeout);
        Assert.Equal(2, o.BulkLaneConnections);
        Assert.True(o.AutoAdjustBulkLanes);
        Assert.Equal(0.3, o.BulkLaneTargetRatio);
        Assert.Equal(TimeSpan.FromSeconds(6), o.BulkLaneResponseTimeout);
        Assert.Equal(0, o.PubSubLaneConnections);
        Assert.Equal(0, o.BlockingLaneConnections);
        Assert.Equal(3, o.MaxScaleEventsPerMinute);
        Assert.Equal(5, o.FlapToggleThreshold);
        Assert.Equal(TimeSpan.FromMinutes(3), o.AutoscaleFreezeDuration);
        Assert.Equal(3.5, o.ReconnectStormFailureRatePerSecThreshold);
    }
}
