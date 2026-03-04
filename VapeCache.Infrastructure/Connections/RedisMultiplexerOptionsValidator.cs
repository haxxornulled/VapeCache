using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisMultiplexerOptionsValidator : IValidateOptions<RedisMultiplexerOptions>
{
    public ValidateOptionsResult Validate(string? name, RedisMultiplexerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string>? failures = null;

        static void AddFailure(ref List<string>? failures, string message)
        {
            failures ??= new List<string>();
            failures.Add(message);
        }

        if (options.Connections <= 0)
            AddFailure(ref failures, "RedisMultiplexer:Connections must be > 0.");

        if (options.MaxInFlightPerConnection <= 0)
            AddFailure(ref failures, "RedisMultiplexer:MaxInFlightPerConnection must be > 0.");

        if (options.CoalescedWriteMaxBytes <= 0)
            AddFailure(ref failures, "RedisMultiplexer:CoalescedWriteMaxBytes must be > 0.");

        if (options.CoalescedWriteMaxSegments <= 0)
            AddFailure(ref failures, "RedisMultiplexer:CoalescedWriteMaxSegments must be > 0.");

        if (options.CoalescedWriteSmallCopyThresholdBytes <= 0)
            AddFailure(ref failures, "RedisMultiplexer:CoalescedWriteSmallCopyThresholdBytes must be > 0.");

        if (options.AdaptiveCoalescingLowDepth <= 0)
            AddFailure(ref failures, "RedisMultiplexer:AdaptiveCoalescingLowDepth must be > 0.");

        if (options.AdaptiveCoalescingHighDepth <= 0)
            AddFailure(ref failures, "RedisMultiplexer:AdaptiveCoalescingHighDepth must be > 0.");

        if (options.AdaptiveCoalescingHighDepth < options.AdaptiveCoalescingLowDepth)
            AddFailure(ref failures, "RedisMultiplexer:AdaptiveCoalescingHighDepth must be >= AdaptiveCoalescingLowDepth.");

        if (options.AdaptiveCoalescingMinWriteBytes <= 0)
            AddFailure(ref failures, "RedisMultiplexer:AdaptiveCoalescingMinWriteBytes must be > 0.");

        if (options.AdaptiveCoalescingMinSegments <= 0)
            AddFailure(ref failures, "RedisMultiplexer:AdaptiveCoalescingMinSegments must be > 0.");

        if (options.AdaptiveCoalescingMinSmallCopyThresholdBytes <= 0)
            AddFailure(ref failures, "RedisMultiplexer:AdaptiveCoalescingMinSmallCopyThresholdBytes must be > 0.");

        if (options.CoalescingEnterQueueDepth <= 0)
            AddFailure(ref failures, "RedisMultiplexer:CoalescingEnterQueueDepth must be > 0.");

        if (options.CoalescingExitQueueDepth <= 0)
            AddFailure(ref failures, "RedisMultiplexer:CoalescingExitQueueDepth must be > 0.");

        if (options.CoalescingExitQueueDepth > options.CoalescingEnterQueueDepth)
            AddFailure(ref failures, "RedisMultiplexer:CoalescingExitQueueDepth must be <= CoalescingEnterQueueDepth.");

        if (options.CoalescedWriteMaxOperations <= 0)
            AddFailure(ref failures, "RedisMultiplexer:CoalescedWriteMaxOperations must be > 0.");

        if (options.CoalescingSpinBudget < 0)
            AddFailure(ref failures, "RedisMultiplexer:CoalescingSpinBudget must be >= 0.");

        if (options.ResponseTimeout < TimeSpan.Zero && options.ResponseTimeout != Timeout.InfiniteTimeSpan)
            AddFailure(ref failures, "RedisMultiplexer:ResponseTimeout must be >= 0 or Timeout.InfiniteTimeSpan.");

        if (options.MinConnections <= 0)
            AddFailure(ref failures, "RedisMultiplexer:MinConnections must be > 0.");

        if (options.MaxConnections <= 0)
            AddFailure(ref failures, "RedisMultiplexer:MaxConnections must be > 0.");

        if (options.MaxConnections < options.MinConnections)
            AddFailure(ref failures, "RedisMultiplexer:MaxConnections must be >= MinConnections.");

        if (options.EnableAutoscaling && (options.Connections < options.MinConnections || options.Connections > options.MaxConnections))
            AddFailure(ref failures, "RedisMultiplexer:Connections must be within [MinConnections, MaxConnections] when autoscaling is enabled.");

        if (options.AutoscaleSampleInterval <= TimeSpan.Zero)
            AddFailure(ref failures, "RedisMultiplexer:AutoscaleSampleInterval must be > 0.");

        if (options.ScaleUpWindow <= TimeSpan.Zero)
            AddFailure(ref failures, "RedisMultiplexer:ScaleUpWindow must be > 0.");

        if (options.ScaleDownWindow <= TimeSpan.Zero)
            AddFailure(ref failures, "RedisMultiplexer:ScaleDownWindow must be > 0.");

        if (options.ScaleUpCooldown <= TimeSpan.Zero)
            AddFailure(ref failures, "RedisMultiplexer:ScaleUpCooldown must be > 0.");

        if (options.ScaleDownCooldown <= TimeSpan.Zero)
            AddFailure(ref failures, "RedisMultiplexer:ScaleDownCooldown must be > 0.");

        if (options.ScaleUpInflightUtilization <= 0 || options.ScaleUpInflightUtilization > 1)
            AddFailure(ref failures, "RedisMultiplexer:ScaleUpInflightUtilization must be in (0,1].");

        if (options.ScaleDownInflightUtilization < 0 || options.ScaleDownInflightUtilization >= 1)
            AddFailure(ref failures, "RedisMultiplexer:ScaleDownInflightUtilization must be in [0,1).");

        if (options.ScaleUpQueueDepthThreshold <= 0)
            AddFailure(ref failures, "RedisMultiplexer:ScaleUpQueueDepthThreshold must be > 0.");

        if (options.ScaleUpTimeoutRatePerSecThreshold <= 0)
            AddFailure(ref failures, "RedisMultiplexer:ScaleUpTimeoutRatePerSecThreshold must be > 0.");

        if (options.ScaleUpP99LatencyMsThreshold <= 0)
            AddFailure(ref failures, "RedisMultiplexer:ScaleUpP99LatencyMsThreshold must be > 0.");

        if (options.ScaleDownP95LatencyMsThreshold <= 0)
            AddFailure(ref failures, "RedisMultiplexer:ScaleDownP95LatencyMsThreshold must be > 0.");

        if (options.BulkLaneConnections < 0 || options.BulkLaneConnections > 2)
            AddFailure(ref failures, "RedisMultiplexer:BulkLaneConnections must be in [0,2].");

        if (options.BulkLaneConnections > 0 && options.Connections <= 1)
            AddFailure(ref failures, "RedisMultiplexer:BulkLaneConnections requires RedisMultiplexer:Connections > 1 for lane isolation.");

        if (options.BulkLaneResponseTimeout <= TimeSpan.Zero)
            AddFailure(ref failures, "RedisMultiplexer:BulkLaneResponseTimeout must be > 0.");

        if (options.BulkLaneConnections > 0 &&
            options.ResponseTimeout > TimeSpan.Zero &&
            options.BulkLaneResponseTimeout < options.ResponseTimeout)
            AddFailure(ref failures, "RedisMultiplexer:BulkLaneResponseTimeout must be >= RedisMultiplexer:ResponseTimeout when bulk lanes are enabled.");

        if (options.EmergencyScaleUpTimeoutRatePerSecThreshold <= 0)
            AddFailure(ref failures, "RedisMultiplexer:EmergencyScaleUpTimeoutRatePerSecThreshold must be > 0.");

        if (options.ScaleDownDrainTimeout <= TimeSpan.Zero)
            AddFailure(ref failures, "RedisMultiplexer:ScaleDownDrainTimeout must be > 0.");

        if (options.MaxScaleEventsPerMinute <= 0)
            AddFailure(ref failures, "RedisMultiplexer:MaxScaleEventsPerMinute must be > 0.");

        if (options.FlapToggleThreshold < 2)
            AddFailure(ref failures, "RedisMultiplexer:FlapToggleThreshold must be >= 2.");

        if (options.AutoscaleFreezeDuration <= TimeSpan.Zero)
            AddFailure(ref failures, "RedisMultiplexer:AutoscaleFreezeDuration must be > 0.");

        if (options.ReconnectStormFailureRatePerSecThreshold <= 0)
            AddFailure(ref failures, "RedisMultiplexer:ReconnectStormFailureRatePerSecThreshold must be > 0.");

        return failures is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
