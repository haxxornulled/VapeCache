using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

internal static class RedisRuntimeOptionsNormalizer
{
    private const int MinPort = 1;
    private const int MaxPort = 65535;
    private const int MinTcpBufferBytes = 4 * 1024;
    private const int MaxTcpBufferBytes = 4 * 1024 * 1024;
    private const int DefaultMaxBulkStringBytes = 16 * 1024 * 1024;
    private const int MinMaxBulkStringBytes = 4 * 1024;
    private const int MaxMaxBulkStringBytes = 256 * 1024 * 1024;
    private const int DefaultMaxArrayDepth = 64;
    private const int MaxArrayDepthUpperBound = 1024;
    private const int MinRespProtocolVersion = 2;
    private const int MaxRespProtocolVersion = 3;
    private const int DefaultRespProtocolVersion = 2;
    private const int DefaultMaxClusterRedirects = 3;
    private const int MaxClusterRedirectsUpperBound = 16;
    private const int MinMultiplexerConnections = 1;
    private const int MaxMultiplexerConnections = 256;
    private const int MinInFlightPerConnection = 64;
    private const int MaxInFlightPerConnection = 131_072;
    private const int MinCoalescedWriteBytes = 16 * 1024;
    private const int MaxCoalescedWriteBytes = 2 * 1024 * 1024;
    private const int MinCoalescedSegments = 16;
    private const int MaxCoalescedSegments = 1024;
    private const int MinSmallCopyThresholdBytes = 64;
    private const int MaxSmallCopyThresholdBytes = 32 * 1024;
    private const int MinAdaptiveDepth = 1;
    private const int MaxAdaptiveDepth = 8192;
    private const int MinAdaptiveWriteBytes = 4 * 1024;
    private const int MinAdaptiveSegments = 1;
    private const int MinCoalescedWriteOperations = 1;
    private const int MaxCoalescedWriteOperations = 2048;
    private const int MinCoalescingSpinBudget = 0;
    private const int MaxCoalescingSpinBudget = 256;
    private const int MinAutoscaleConnections = 1;
    private const int MaxAutoscaleConnections = 256;
    private const int MinBulkLaneConnections = 0;
    private const int MaxBulkLaneConnections = MaxMultiplexerConnections - 1;
    private const int MinReservedRoleLaneConnections = 0;
    private const double MinPositiveThreshold = 0.01d;
    private const double MinBulkLaneTargetRatio = 0d;
    private const double MaxBulkLaneTargetRatio = 0.90d;
    private const double DefaultBulkLaneTargetRatio = 0.25d;

    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultAcquireTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultTcpKeepAliveTime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultTcpKeepAliveInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultAutoscaleSampleInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultScaleUpWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultScaleDownWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultScaleUpCooldown = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultScaleDownCooldown = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DefaultScaleDownDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultAutoscaleFreezeDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultBulkLaneResponseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Normalizes value.
    /// </summary>
    public static RedisConnectionOptions NormalizeConnection(RedisConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var transportProfile = NormalizeTransportProfile(options.TransportProfile);
        var profiled = RedisTransportProfiles.Apply(options with { TransportProfile = transportProfile });

        var host = HasText(profiled.Host) ? profiled.Host : "localhost";
        var port = NormalizeInt(profiled.Port, MinPort, MaxPort, fallbackWhenInvalid: 6379);
        var maxConnections = NormalizeInt(profiled.MaxConnections, 1, 8192, fallbackWhenInvalid: 1);
        var maxIdle = NormalizeInt(profiled.MaxIdle, 1, maxConnections, fallbackWhenInvalid: maxConnections);
        var warm = NormalizeInt(profiled.Warm, 0, maxIdle, fallbackWhenInvalid: maxIdle, fallbackWhenNonPositive: 0);
        var connectTimeout = NormalizePositive(profiled.ConnectTimeout, DefaultConnectTimeout);
        var acquireTimeout = NormalizePositive(profiled.AcquireTimeout, DefaultAcquireTimeout);
        var validateAfterIdle = NormalizeNonNegative(profiled.ValidateAfterIdle);
        var validateTimeout = NormalizeNonNegative(profiled.ValidateTimeout);
        var idleTimeout = NormalizeNonNegative(profiled.IdleTimeout);
        var maxConnectionLifetime = NormalizeNonNegative(profiled.MaxConnectionLifetime);
        var reaperPeriod = NormalizeNonNegative(profiled.ReaperPeriod);
        var tcpSendBufferBytes = NormalizeTcpBufferBytes(profiled.TcpSendBufferBytes);
        var tcpReceiveBufferBytes = NormalizeTcpBufferBytes(profiled.TcpReceiveBufferBytes);
        var tcpKeepAliveTime = NormalizePositive(profiled.TcpKeepAliveTime, DefaultTcpKeepAliveTime);
        var tcpKeepAliveInterval = NormalizePositive(profiled.TcpKeepAliveInterval, DefaultTcpKeepAliveInterval);
        var maxBulkStringBytes = NormalizeMaxBulkStringBytes(profiled.MaxBulkStringBytes);
        var maxArrayDepth = NormalizeMaxArrayDepth(profiled.MaxArrayDepth);
        var respProtocolVersion = NormalizeRespProtocolVersion(profiled.RespProtocolVersion);
        var maxClusterRedirects = NormalizeMaxClusterRedirects(profiled.MaxClusterRedirects);

        return profiled with
        {
            Host = host,
            Port = port,
            MaxConnections = maxConnections,
            MaxIdle = maxIdle,
            Warm = warm,
            ConnectTimeout = connectTimeout,
            AcquireTimeout = acquireTimeout,
            ValidateAfterIdle = validateAfterIdle,
            ValidateTimeout = validateTimeout,
            IdleTimeout = idleTimeout,
            MaxConnectionLifetime = maxConnectionLifetime,
            ReaperPeriod = reaperPeriod,
            TcpSendBufferBytes = tcpSendBufferBytes,
            TcpReceiveBufferBytes = tcpReceiveBufferBytes,
            TcpKeepAliveTime = tcpKeepAliveTime,
            TcpKeepAliveInterval = tcpKeepAliveInterval,
            MaxBulkStringBytes = maxBulkStringBytes,
            MaxArrayDepth = maxArrayDepth,
            RespProtocolVersion = respProtocolVersion,
            MaxClusterRedirects = maxClusterRedirects
        };
    }

    /// <summary>
    /// Normalizes value.
    /// </summary>
    public static RedisMultiplexerOptions NormalizeMultiplexer(RedisMultiplexerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var transportProfile = NormalizeTransportProfile(options.TransportProfile);
        var profiled = RedisTransportProfiles.Apply(options with { TransportProfile = transportProfile });

        var minConnections = NormalizeInt(profiled.MinConnections, MinAutoscaleConnections, MaxAutoscaleConnections, fallbackWhenInvalid: MinAutoscaleConnections);
        var maxConnections = NormalizeInt(
            Math.Max(profiled.MaxConnections, minConnections),
            minConnections,
            MaxAutoscaleConnections,
            fallbackWhenInvalid: minConnections);

        var connections = NormalizeInt(profiled.Connections, MinMultiplexerConnections, MaxMultiplexerConnections, fallbackWhenInvalid: MinMultiplexerConnections);
        if (profiled.EnableAutoscaling)
            connections = Math.Clamp(connections, minConnections, maxConnections);

        var maxInFlightPerConnection = NormalizeInt(profiled.MaxInFlightPerConnection, MinInFlightPerConnection, MaxInFlightPerConnection, fallbackWhenInvalid: MinInFlightPerConnection);
        var coalescedWriteMaxBytes = NormalizeInt(profiled.CoalescedWriteMaxBytes, MinCoalescedWriteBytes, MaxCoalescedWriteBytes, fallbackWhenInvalid: MinCoalescedWriteBytes);
        var coalescedWriteMaxSegments = NormalizeInt(profiled.CoalescedWriteMaxSegments, MinCoalescedSegments, MaxCoalescedSegments, fallbackWhenInvalid: MinCoalescedSegments);
        var coalescedWriteSmallCopyThresholdBytes = NormalizeInt(
            profiled.CoalescedWriteSmallCopyThresholdBytes,
            MinSmallCopyThresholdBytes,
            Math.Min(MaxSmallCopyThresholdBytes, coalescedWriteMaxBytes),
            fallbackWhenInvalid: MinSmallCopyThresholdBytes);

        var adaptiveCoalescingLowDepth = NormalizeInt(profiled.AdaptiveCoalescingLowDepth, MinAdaptiveDepth, MaxAdaptiveDepth, fallbackWhenInvalid: MinAdaptiveDepth);
        var adaptiveCoalescingHighDepth = NormalizeInt(
            profiled.AdaptiveCoalescingHighDepth,
            adaptiveCoalescingLowDepth,
            MaxAdaptiveDepth,
            fallbackWhenInvalid: adaptiveCoalescingLowDepth);
        var adaptiveCoalescingMinWriteBytes = NormalizeInt(
            profiled.AdaptiveCoalescingMinWriteBytes,
            MinAdaptiveWriteBytes,
            coalescedWriteMaxBytes,
            fallbackWhenInvalid: MinAdaptiveWriteBytes);
        var adaptiveCoalescingMinSegments = NormalizeInt(
            profiled.AdaptiveCoalescingMinSegments,
            MinAdaptiveSegments,
            coalescedWriteMaxSegments,
            fallbackWhenInvalid: MinAdaptiveSegments);
        var adaptiveCoalescingMinSmallCopyThresholdBytes = NormalizeInt(
            profiled.AdaptiveCoalescingMinSmallCopyThresholdBytes,
            MinSmallCopyThresholdBytes,
            coalescedWriteSmallCopyThresholdBytes,
            fallbackWhenInvalid: MinSmallCopyThresholdBytes);
        var coalescingEnterQueueDepth = NormalizeInt(
            profiled.CoalescingEnterQueueDepth,
            MinAdaptiveDepth,
            MaxAdaptiveDepth,
            fallbackWhenInvalid: 8);
        var coalescingExitQueueDepth = NormalizeInt(
            profiled.CoalescingExitQueueDepth,
            MinAdaptiveDepth,
            coalescingEnterQueueDepth,
            fallbackWhenInvalid: Math.Min(3, coalescingEnterQueueDepth));
        var coalescedWriteMaxOperations = NormalizeInt(
            profiled.CoalescedWriteMaxOperations,
            MinCoalescedWriteOperations,
            MaxCoalescedWriteOperations,
            fallbackWhenInvalid: 128);
        var coalescingSpinBudget = NormalizeInt(
            profiled.CoalescingSpinBudget,
            MinCoalescingSpinBudget,
            MaxCoalescingSpinBudget,
            fallbackWhenInvalid: 8,
            fallbackWhenNonPositive: 0);

        var responseTimeout = NormalizeTimeout(profiled.ResponseTimeout);
        var bulkLaneConnections = NormalizeBulkLaneConnections(profiled.BulkLaneConnections);
        var autoAdjustBulkLanes = profiled.AutoAdjustBulkLanes;
        var bulkLaneTargetRatio = NormalizeBulkLaneTargetRatio(profiled.BulkLaneTargetRatio);
        var bulkLaneResponseTimeout = NormalizePositive(profiled.BulkLaneResponseTimeout, DefaultBulkLaneResponseTimeout);
        var pubSubLaneConnections = Math.Max(MinReservedRoleLaneConnections, profiled.PubSubLaneConnections);
        var blockingLaneConnections = Math.Max(MinReservedRoleLaneConnections, profiled.BlockingLaneConnections);
        var autoscaleSampleInterval = NormalizePositive(profiled.AutoscaleSampleInterval, DefaultAutoscaleSampleInterval);
        var scaleUpWindow = NormalizePositive(profiled.ScaleUpWindow, DefaultScaleUpWindow);
        var scaleDownWindow = NormalizePositive(profiled.ScaleDownWindow, DefaultScaleDownWindow);
        var scaleUpCooldown = NormalizePositive(profiled.ScaleUpCooldown, DefaultScaleUpCooldown);
        var scaleDownCooldown = NormalizePositive(profiled.ScaleDownCooldown, DefaultScaleDownCooldown);
        var scaleUpInflightUtilization = Math.Clamp(profiled.ScaleUpInflightUtilization, 0.10, 0.98);
        var scaleDownInflightUtilization = NormalizeScaleDownInflightUtilization(
            profiled.ScaleDownInflightUtilization,
            scaleUpInflightUtilization);
        var scaleUpQueueDepthThreshold = Math.Max(1, profiled.ScaleUpQueueDepthThreshold);
        var scaleUpTimeoutRatePerSecThreshold = Math.Max(MinPositiveThreshold, profiled.ScaleUpTimeoutRatePerSecThreshold);
        var scaleUpP99LatencyMsThreshold = Math.Max(1.0, profiled.ScaleUpP99LatencyMsThreshold);
        var scaleDownP95LatencyMsThreshold = NormalizeScaleDownP95LatencyMsThreshold(
            profiled.ScaleDownP95LatencyMsThreshold,
            scaleUpP99LatencyMsThreshold);
        var emergencyScaleUpTimeoutRatePerSecThreshold = Math.Max(
            Math.Max(MinPositiveThreshold, profiled.EmergencyScaleUpTimeoutRatePerSecThreshold),
            scaleUpTimeoutRatePerSecThreshold);
        var scaleDownDrainTimeout = NormalizePositive(profiled.ScaleDownDrainTimeout, DefaultScaleDownDrainTimeout);
        var maxScaleEventsPerMinute = Math.Max(1, profiled.MaxScaleEventsPerMinute);
        var flapToggleThreshold = Math.Max(2, profiled.FlapToggleThreshold);
        var autoscaleFreezeDuration = NormalizePositive(profiled.AutoscaleFreezeDuration, DefaultAutoscaleFreezeDuration);
        var reconnectStormFailureRatePerSecThreshold = Math.Max(MinPositiveThreshold, profiled.ReconnectStormFailureRatePerSecThreshold);
        if (scaleDownWindow < scaleUpWindow)
            scaleDownWindow = scaleUpWindow;
        if (scaleDownCooldown < scaleUpCooldown)
            scaleDownCooldown = scaleUpCooldown;

        return profiled with
        {
            Connections = connections,
            MaxInFlightPerConnection = maxInFlightPerConnection,
            CoalescedWriteMaxBytes = coalescedWriteMaxBytes,
            CoalescedWriteMaxSegments = coalescedWriteMaxSegments,
            CoalescedWriteSmallCopyThresholdBytes = coalescedWriteSmallCopyThresholdBytes,
            AdaptiveCoalescingLowDepth = adaptiveCoalescingLowDepth,
            AdaptiveCoalescingHighDepth = adaptiveCoalescingHighDepth,
            AdaptiveCoalescingMinWriteBytes = adaptiveCoalescingMinWriteBytes,
            AdaptiveCoalescingMinSegments = adaptiveCoalescingMinSegments,
            AdaptiveCoalescingMinSmallCopyThresholdBytes = adaptiveCoalescingMinSmallCopyThresholdBytes,
            CoalescingEnterQueueDepth = coalescingEnterQueueDepth,
            CoalescingExitQueueDepth = coalescingExitQueueDepth,
            CoalescedWriteMaxOperations = coalescedWriteMaxOperations,
            CoalescingSpinBudget = coalescingSpinBudget,
            ResponseTimeout = responseTimeout,
            BulkLaneConnections = bulkLaneConnections,
            AutoAdjustBulkLanes = autoAdjustBulkLanes,
            BulkLaneTargetRatio = bulkLaneTargetRatio,
            BulkLaneResponseTimeout = bulkLaneResponseTimeout,
            PubSubLaneConnections = pubSubLaneConnections,
            BlockingLaneConnections = blockingLaneConnections,
            MinConnections = minConnections,
            MaxConnections = maxConnections,
            AutoscaleSampleInterval = autoscaleSampleInterval,
            ScaleUpWindow = scaleUpWindow,
            ScaleDownWindow = scaleDownWindow,
            ScaleUpCooldown = scaleUpCooldown,
            ScaleDownCooldown = scaleDownCooldown,
            ScaleUpInflightUtilization = scaleUpInflightUtilization,
            ScaleDownInflightUtilization = scaleDownInflightUtilization,
            ScaleUpQueueDepthThreshold = scaleUpQueueDepthThreshold,
            ScaleUpTimeoutRatePerSecThreshold = scaleUpTimeoutRatePerSecThreshold,
            ScaleUpP99LatencyMsThreshold = scaleUpP99LatencyMsThreshold,
            ScaleDownP95LatencyMsThreshold = scaleDownP95LatencyMsThreshold,
            EmergencyScaleUpTimeoutRatePerSecThreshold = emergencyScaleUpTimeoutRatePerSecThreshold,
            ScaleDownDrainTimeout = scaleDownDrainTimeout,
            MaxScaleEventsPerMinute = maxScaleEventsPerMinute,
            FlapToggleThreshold = flapToggleThreshold,
            AutoscaleFreezeDuration = autoscaleFreezeDuration,
            ReconnectStormFailureRatePerSecThreshold = reconnectStormFailureRatePerSecThreshold
        };
    }

    private static RedisTransportProfile NormalizeTransportProfile(RedisTransportProfile profile)
    {
        return Enum.IsDefined(profile)
            ? profile
            : RedisTransportProfile.FullTilt;
    }

    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);

    private static int NormalizeInt(int value, int min, int max, int fallbackWhenInvalid, int? fallbackWhenNonPositive = null)
    {
        if (value < min || value > max)
        {
            if (value <= 0)
            {
                var fallback = fallbackWhenNonPositive ?? fallbackWhenInvalid;
                return Math.Clamp(fallback, min, max);
            }

            return Math.Clamp(value, min, max);
        }

        return value;
    }

    private static int NormalizeTcpBufferBytes(int configuredBytes)
    {
        if (configuredBytes <= 0)
            return 0;

        return NormalizeInt(configuredBytes, MinTcpBufferBytes, MaxTcpBufferBytes, fallbackWhenInvalid: MinTcpBufferBytes);
    }

    private static int NormalizeMaxBulkStringBytes(int configuredBytes)
    {
        if (configuredBytes == -1)
            return -1;

        if (configuredBytes <= 0)
            return DefaultMaxBulkStringBytes;

        return NormalizeInt(configuredBytes, MinMaxBulkStringBytes, MaxMaxBulkStringBytes, fallbackWhenInvalid: DefaultMaxBulkStringBytes);
    }

    private static int NormalizeMaxArrayDepth(int configuredDepth)
    {
        if (configuredDepth == -1)
            return -1;

        if (configuredDepth <= 0)
            return DefaultMaxArrayDepth;

        return NormalizeInt(configuredDepth, 1, MaxArrayDepthUpperBound, fallbackWhenInvalid: DefaultMaxArrayDepth);
    }

    private static int NormalizeRespProtocolVersion(int configuredVersion)
        => NormalizeInt(
            configuredVersion,
            MinRespProtocolVersion,
            MaxRespProtocolVersion,
            fallbackWhenInvalid: DefaultRespProtocolVersion);

    private static int NormalizeMaxClusterRedirects(int configuredRedirects)
        => NormalizeInt(
            configuredRedirects,
            0,
            MaxClusterRedirectsUpperBound,
            fallbackWhenInvalid: DefaultMaxClusterRedirects,
            fallbackWhenNonPositive: 0);

    private static TimeSpan NormalizePositive(TimeSpan value, TimeSpan fallback)
        => value <= TimeSpan.Zero ? fallback : value;

    private static TimeSpan NormalizeNonNegative(TimeSpan value)
        => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static TimeSpan NormalizeTimeout(TimeSpan value)
        => value == Timeout.InfiniteTimeSpan || value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static int NormalizeBulkLaneConnections(int value)
    {
        if (value < MinBulkLaneConnections)
            return 1;

        return Math.Clamp(value, MinBulkLaneConnections, MaxBulkLaneConnections);
    }

    private static double NormalizeBulkLaneTargetRatio(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return DefaultBulkLaneTargetRatio;

        return Math.Clamp(value, MinBulkLaneTargetRatio, MaxBulkLaneTargetRatio);
    }

    private static double NormalizeScaleDownInflightUtilization(double configuredScaleDown, double normalizedScaleUp)
    {
        var normalizedScaleDown = Math.Clamp(configuredScaleDown, 0.01, 0.70);
        var maxScaleDown = Math.Max(0.01, normalizedScaleUp - 0.01);
        return Math.Min(normalizedScaleDown, maxScaleDown);
    }

    private static double NormalizeScaleDownP95LatencyMsThreshold(double configuredScaleDownP95, double normalizedScaleUpP99)
    {
        var normalizedScaleDownP95 = Math.Max(0.5, configuredScaleDownP95);
        return Math.Min(normalizedScaleDownP95, normalizedScaleUpP99);
    }
}
