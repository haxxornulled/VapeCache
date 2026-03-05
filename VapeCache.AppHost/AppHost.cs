var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("redis");

builder.AddProject<Projects.VapeCache_UI>("vapecache-ui")
    .WithReference(redis)
    .WaitFor(redis);

builder.Build().Run();
