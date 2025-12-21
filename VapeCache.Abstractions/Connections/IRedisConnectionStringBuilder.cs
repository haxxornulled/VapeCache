namespace VapeCache.Abstractions.Connections;

public interface IRedisConnectionStringBuilder
{
    string Build(RedisConnectionOptions options);
}
