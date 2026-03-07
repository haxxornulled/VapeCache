var builder = DistributedApplication.CreateBuilder(args);

var sharedRedisConnectionString = Environment.GetEnvironmentVariable("VAPECACHE_REDIS_CONNECTIONSTRING");
if (!string.IsNullOrWhiteSpace(sharedRedisConnectionString))
{
    builder.Configuration["ConnectionStrings:redis"] = sharedRedisConnectionString;
}

var useContainerRedis =
    bool.TryParse(Environment.GetEnvironmentVariable("VAPECACHE_USE_CONTAINER_REDIS"), out var parsedUseContainerRedis) &&
    parsedUseContainerRedis;
var includeConsoleLoad =
    !bool.TryParse(Environment.GetEnvironmentVariable("VAPECACHE_INCLUDE_CONSOLE_LOAD"), out var parsedIncludeConsoleLoad) ||
    parsedIncludeConsoleLoad;

if (useContainerRedis)
{
    var redis = builder.AddRedis("redis");

    builder.AddProject<Projects.VapeCache_UI>("vapecache-ui")
        .WithReference(redis)
        .WaitFor(redis)
        .WithExternalHttpEndpoints();

    if (includeConsoleLoad)
    {
        builder.AddProject<Projects.VapeCache_Console>("vapecache-load")
            .WithReference(redis)
            .WaitFor(redis);
    }
}
else
{
    var redis = builder.AddConnectionString("redis");

    builder.AddProject<Projects.VapeCache_UI>("vapecache-ui")
        .WithReference(redis)
        .WithExternalHttpEndpoints();

    if (includeConsoleLoad)
    {
        builder.AddProject<Projects.VapeCache_Console>("vapecache-load")
            .WithReference(redis);
    }
}

builder.Build().Run();
