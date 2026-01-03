using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Collections;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;

var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["RedisConnection:Host"] = "localhost",
        ["RedisConnection:Port"] = "6379"
    })
    .Build();

var services = new ServiceCollection();
services.AddVapecacheRedisConnections();
services.AddVapecacheCaching();
services.AddOptions<RedisConnectionOptions>()
    .Bind(configuration.GetSection("RedisConnection"));

var provider = services.BuildServiceProvider();

if (!string.Equals(Environment.GetEnvironmentVariable("VAPECACHE_SAMPLE_RUN"), "true", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Set VAPECACHE_SAMPLE_RUN=true to execute sample operations.");
    return;
}

var cache = provider.GetRequiredService<ICacheService>();
await cache.SetAsync(
    "sample:raw",
    "hello"u8.ToArray(),
    new CacheEntryOptions(TimeSpan.FromMinutes(5)),
    CancellationToken.None);

var bytes = await cache.GetAsync("sample:raw", CancellationToken.None);
Console.WriteLine(bytes is null ? "cache miss" : $"cache hit: {System.Text.Encoding.UTF8.GetString(bytes)}");

var collections = provider.GetRequiredService<ICacheCollectionFactory>();
var queue = collections.List<string>("sample:queue");
await queue.PushBackAsync("job-1");
var job = await queue.PopFrontAsync();
Console.WriteLine(job is null ? "queue empty" : $"queue item: {job}");
