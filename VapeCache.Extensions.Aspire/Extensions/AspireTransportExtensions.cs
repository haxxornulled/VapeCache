using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Transport tuning extensions for Aspire-hosted VapeCache services.
/// </summary>
public static class AspireTransportExtensions
{
    private static readonly PropertyInfo ConnectionTransportProfileProperty =
        typeof(RedisConnectionOptions).GetProperty(nameof(RedisConnectionOptions.TransportProfile))!;

    private static readonly PropertyInfo MultiplexerTransportProfileProperty =
        typeof(RedisMultiplexerOptions).GetProperty(nameof(RedisMultiplexerOptions.TransportProfile))!;

    private static readonly PropertyInfo MultiplexerDedicatedWorkersProperty =
        typeof(RedisMultiplexerOptions).GetProperty(nameof(RedisMultiplexerOptions.UseDedicatedLaneWorkers))!;

    private static readonly PropertyInfo MultiplexerSocketReaderProperty =
        typeof(RedisMultiplexerOptions).GetProperty(nameof(RedisMultiplexerOptions.EnableSocketRespReader))!;

    private static readonly PropertyInfo MultiplexerCoalescedWritesProperty =
        typeof(RedisMultiplexerOptions).GetProperty(nameof(RedisMultiplexerOptions.EnableCoalescedSocketWrites))!;

    /// <summary>
    /// Applies a high-level transport mode to Redis connection and multiplexer options.
    /// </summary>
    public static AspireVapeCacheBuilder UseTransport(
        this AspireVapeCacheBuilder builder,
        VapeCacheAspireTransportMode mode)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var profile = mode.ToRedisTransportProfile();

        builder.Builder.Services.PostConfigure<RedisConnectionOptions>(options =>
        {
            ConnectionTransportProfileProperty.SetValue(options, profile);
        });

        builder.Builder.Services.PostConfigure<RedisMultiplexerOptions>(options =>
        {
            MultiplexerTransportProfileProperty.SetValue(options, profile);

            switch (mode)
            {
                case VapeCacheAspireTransportMode.MaxThroughput:
                    MultiplexerCoalescedWritesProperty.SetValue(options, true);
                    MultiplexerDedicatedWorkersProperty.SetValue(options, true);
                    MultiplexerSocketReaderProperty.SetValue(options, true);
                    break;

                case VapeCacheAspireTransportMode.Balanced:
                    MultiplexerCoalescedWritesProperty.SetValue(options, true);
                    MultiplexerDedicatedWorkersProperty.SetValue(options, false);
                    MultiplexerSocketReaderProperty.SetValue(options, false);
                    break;

                case VapeCacheAspireTransportMode.UltraLowLatency:
                    MultiplexerCoalescedWritesProperty.SetValue(options, true);
                    MultiplexerDedicatedWorkersProperty.SetValue(options, false);
                    MultiplexerSocketReaderProperty.SetValue(options, true);
                    break;
            }
        });

        return builder;
    }
}

