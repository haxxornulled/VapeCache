namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Defines the redis connection string builder contract.
/// </summary>
public interface IRedisConnectionStringBuilder
{
    /// <summary>
    /// Executes build.
    /// </summary>
    string Build(RedisConnectionOptions options);
}
