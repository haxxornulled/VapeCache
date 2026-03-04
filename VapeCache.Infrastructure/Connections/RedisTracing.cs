using System.Diagnostics;

namespace VapeCache.Infrastructure.Connections;

internal static class RedisTracing
{
    public static readonly ActivitySource ActivitySource = new("VapeCache.Redis");

    public static Activity? StartConnect()
    {
        if (!ActivitySource.HasListeners())
            return null;

        var activity = ActivitySource.StartActivity("redis.connect", ActivityKind.Client);
        activity?.SetTag("db.system", "redis");
        return activity;
    }

    public static Activity? StartCommand(string operation, bool instrumentationEnabled)
    {
        if (!instrumentationEnabled || !ActivitySource.HasListeners())
            return null;

        var activity = ActivitySource.StartActivity("redis.command", ActivityKind.Client);
        if (activity is null)
            return null;

        activity.SetTag("db.system", "redis");
        activity.SetTag("db.operation", operation);
        return activity;
    }
}
