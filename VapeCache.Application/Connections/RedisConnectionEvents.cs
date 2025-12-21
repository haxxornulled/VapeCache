namespace VapeCache.Application.Connections;

public readonly record struct RedisConnectAttempt(string Host, int Port, bool UseTls);

public readonly record struct RedisConnected(
    long ConnectionId,
    string Host,
    int Port,
    bool UseTls,
    TimeSpan ConnectTime);

public readonly record struct RedisConnectFailed(string Host, int Port, bool UseTls, Exception Exception);

public readonly record struct RedisAuthenticated(long ConnectionId, string? Username, bool UsedFallbackPasswordOnly);

public readonly record struct RedisDatabaseSelected(long ConnectionId, int Database);

public interface IRedisConnectionObserver
{
    void OnConnectAttempt(in RedisConnectAttempt e) { }
    void OnConnected(in RedisConnected e) { }
    void OnConnectFailed(in RedisConnectFailed e) { }
    void OnAuthenticated(in RedisAuthenticated e) { }
    void OnDatabaseSelected(in RedisDatabaseSelected e) { }
}

