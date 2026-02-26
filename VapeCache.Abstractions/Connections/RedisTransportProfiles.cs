namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Applies named Redis transport profiles to connection and multiplexer options.
/// </summary>
public static class RedisTransportProfiles
{
    /// <summary>
    /// Executes value.
    /// </summary>
    public static RedisConnectionOptions Apply(RedisConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.TransportProfile switch
        {
            RedisTransportProfile.FullTilt => options,
            RedisTransportProfile.Balanced => options with
            {
                EnableTcpNoDelay = true,
                TcpSendBufferBytes = 1024 * 1024,
                TcpReceiveBufferBytes = 1024 * 1024
            },
            RedisTransportProfile.LowLatency => options with
            {
                EnableTcpNoDelay = true,
                TcpSendBufferBytes = 256 * 1024,
                TcpReceiveBufferBytes = 256 * 1024
            },
            _ => options
        };
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public static RedisMultiplexerOptions Apply(RedisMultiplexerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.TransportProfile switch
        {
            RedisTransportProfile.FullTilt => options with
            {
                EnableCoalescedSocketWrites = options.EnableCoalescedSocketWrites,
                CoalescedWriteMaxBytes = 512 * 1024,
                CoalescedWriteMaxSegments = 192,
                CoalescedWriteSmallCopyThresholdBytes = 1536,
                EnableAdaptiveCoalescing = options.EnableAdaptiveCoalescing,
                AdaptiveCoalescingLowDepth = 6,
                AdaptiveCoalescingHighDepth = 56,
                AdaptiveCoalescingMinWriteBytes = 64 * 1024,
                AdaptiveCoalescingMinSegments = 48,
                AdaptiveCoalescingMinSmallCopyThresholdBytes = 384
            },
            RedisTransportProfile.Balanced => options with
            {
                EnableCoalescedSocketWrites = options.EnableCoalescedSocketWrites,
                CoalescedWriteMaxBytes = 256 * 1024,
                CoalescedWriteMaxSegments = 128,
                CoalescedWriteSmallCopyThresholdBytes = 1024,
                EnableAdaptiveCoalescing = options.EnableAdaptiveCoalescing,
                AdaptiveCoalescingLowDepth = 4,
                AdaptiveCoalescingHighDepth = 48,
                AdaptiveCoalescingMinWriteBytes = 32 * 1024,
                AdaptiveCoalescingMinSegments = 32,
                AdaptiveCoalescingMinSmallCopyThresholdBytes = 512
            },
            RedisTransportProfile.LowLatency => options with
            {
                EnableCoalescedSocketWrites = options.EnableCoalescedSocketWrites,
                CoalescedWriteMaxBytes = 64 * 1024,
                CoalescedWriteMaxSegments = 64,
                CoalescedWriteSmallCopyThresholdBytes = 512,
                EnableAdaptiveCoalescing = options.EnableAdaptiveCoalescing,
                AdaptiveCoalescingLowDepth = 2,
                AdaptiveCoalescingHighDepth = 24,
                AdaptiveCoalescingMinWriteBytes = 16 * 1024,
                AdaptiveCoalescingMinSegments = 16,
                AdaptiveCoalescingMinSmallCopyThresholdBytes = 256
            },
            _ => options
        };
    }
}
