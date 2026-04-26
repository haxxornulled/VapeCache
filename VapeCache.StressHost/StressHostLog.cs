using Microsoft.Extensions.Logging;

namespace VapeCache.StressHost;

internal static partial class StressHostLog
{
    [LoggerMessage(
        EventId = 31010,
        Level = LogLevel.Information,
        Message = "VapeCache.StressHost starting in {Environment}. Redis endpoint source resolved to {RedisConnectionString}")]
    public static partial void LogStarting(ILogger logger, string environment, string redisConnectionString);
}
