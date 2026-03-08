namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Represents the struct.
/// </summary>
public readonly record struct RedisConnectAttempt(string Host, int Port, bool UseTls);

/// <summary>
/// Represents the struct.
/// </summary>
public readonly record struct RedisConnected(
    long ConnectionId,
    string Host,
    int Port,
    bool UseTls,
    TimeSpan ConnectTime);

/// <summary>
/// Represents the struct.
/// </summary>
public readonly record struct RedisConnectFailed(string Host, int Port, bool UseTls, Exception Exception);

/// <summary>
/// Represents the struct.
/// </summary>
public readonly record struct RedisAuthenticated(long ConnectionId, string? Username, bool UsedFallbackPasswordOnly);

/// <summary>
/// Represents the struct.
/// </summary>
public readonly record struct RedisDatabaseSelected(long ConnectionId, int Database);

/// <summary>
/// Defines the redis connection observer contract.
/// </summary>
public interface IRedisConnectionObserver
{
    /// <summary>
    /// Executes on connect attempt.
    /// </summary>
    void OnConnectAttempt(in RedisConnectAttempt e) { }
    /// <summary>
    /// Executes on connected.
    /// </summary>
    void OnConnected(in RedisConnected e) { }
    /// <summary>
    /// Executes on connect failed.
    /// </summary>
    void OnConnectFailed(in RedisConnectFailed e) { }
    /// <summary>
    /// Executes on authenticated.
    /// </summary>
    void OnAuthenticated(in RedisAuthenticated e) { }
    /// <summary>
    /// Executes on database selected.
    /// </summary>
    void OnDatabaseSelected(in RedisDatabaseSelected e) { }
}
