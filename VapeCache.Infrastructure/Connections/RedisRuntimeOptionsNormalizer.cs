using VapeCache.Abstractions.Connections;
using VapeCache.Application.Guards;

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
    private const int MinAutoscaleConnections = 1;
    private const int MaxAutoscaleConnections = 256;
    private const double MinPositiveThreshold = 0.01d;

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
            MaxArrayDepth = maxArrayDepth
        };
    }

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

        var responseTimeout = NormalizeTimeout(profiled.ResponseTimeout);
        var autoscaleSampleInterval = NormalizePositive(profiled.AutoscaleSampleInterval, DefaultAutoscaleSampleInterval);
        var scaleUpWindow = NormalizePositive(profiled.ScaleUpWindow, DefaultScaleUpWindow);
        var scaleDownWindow = NormalizePositive(profiled.ScaleDownWindow, DefaultScaleDownWindow);
        var scaleUpCooldown = NormalizePositive(profiled.ScaleUpCooldown, DefaultScaleUpCooldown);
        var scaleDownCooldown = NormalizePositive(profiled.ScaleDownCooldown, DefaultScaleDownCooldown);
        var scaleUpInflightUtilization = Math.Clamp(profiled.ScaleUpInflightUtilization, 0.10, 0.98);
        var scaleDownInflightUtilization = Math.Clamp(profiled.ScaleDownInflightUtilization, 0.01, 0.70);
        var scaleUpQueueDepthThreshold = Math.Max(1, profiled.ScaleUpQueueDepthThreshold);
        var scaleUpTimeoutRatePerSecThreshold = Math.Max(MinPositiveThreshold, profiled.ScaleUpTimeoutRatePerSecThreshold);
        var scaleUpP99LatencyMsThreshold = Math.Max(1.0, profiled.ScaleUpP99LatencyMsThreshold);
        var scaleDownP95LatencyMsThreshold = Math.Max(0.5, profiled.ScaleDownP95LatencyMsThreshold);
        var emergencyScaleUpTimeoutRatePerSecThreshold = Math.Max(MinPositiveThreshold, profiled.EmergencyScaleUpTimeoutRatePerSecThreshold);
        var scaleDownDrainTimeout = NormalizePositive(profiled.ScaleDownDrainTimeout, DefaultScaleDownDrainTimeout);
        var maxScaleEventsPerMinute = Math.Max(1, profiled.MaxScaleEventsPerMinute);
        var flapToggleThreshold = Math.Max(2, profiled.FlapToggleThreshold);
        var autoscaleFreezeDuration = NormalizePositive(profiled.AutoscaleFreezeDuration, DefaultAutoscaleFreezeDuration);
        var reconnectStormFailureRatePerSecThreshold = Math.Max(MinPositiveThreshold, profiled.ReconnectStormFailureRatePerSecThreshold);

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
            ResponseTimeout = responseTimeout,
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
        try
        {
            return Guard.Against.ValidEnumValue(profile);
        }
        catch (ArgumentException)
        {
            return RedisTransportProfile.FullTilt;
        }
    }

    private static bool HasText(string? value)
    {
        try
        {
            Guard.Against.NotNullOrWhiteSpace(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static int NormalizeInt(int value, int min, int max, int fallbackWhenInvalid, int? fallbackWhenNonPositive = null)
    {
        try
        {
            return Guard.Against.NotOutOfRange(value, min, max);
        }
        catch (ArgumentOutOfRangeException)
        {
            if (value <= 0)
            {
                var fallback = fallbackWhenNonPositive ?? fallbackWhenInvalid;
                return Math.Clamp(fallback, min, max);
            }

            return Math.Clamp(value, min, max);
        }
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

    private static TimeSpan NormalizePositive(TimeSpan value, TimeSpan fallback)
        => value <= TimeSpan.Zero ? fallback : value;

    private static TimeSpan NormalizeNonNegative(TimeSpan value)
        => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static TimeSpan NormalizeTimeout(TimeSpan value)
        => value == Timeout.InfiniteTimeSpan || value < TimeSpan.Zero ? TimeSpan.Zero : value;
}
