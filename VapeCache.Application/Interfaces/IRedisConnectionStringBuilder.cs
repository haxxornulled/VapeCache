namespace VapeCache.Application.Connections;

public interface IRedisConnectionStringBuilder
{
    string Build(RedisConnectionOptions options);
}

