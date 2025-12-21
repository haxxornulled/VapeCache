namespace VapeCache.Console.Secrets;

public sealed record RedisSecretOptions
{
    public string EnvVar { get; init; } = "VAPECACHE_REDIS_CONNECTIONSTRING";
    public bool Required { get; init; }
}

