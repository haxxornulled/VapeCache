namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Represents the struct.
/// </summary>
public readonly record struct RedisConnectAttempt
{
    public RedisConnectAttempt(string host, int port, bool useTls)
    {
        Host = host;
        Port = port;
        UseTls = useTls;
    }

    public string Host { get; init; }
    public int Port { get; init; }
    public bool UseTls { get; init; }
}

/// <summary>
/// Represents the struct.
/// </summary>
public readonly record struct RedisConnected
{
    public RedisConnected(long connectionId, string host, int port, bool useTls, TimeSpan connectTime)
    {
        ConnectionId = connectionId;
        Host = host;
        Port = port;
        UseTls = useTls;
        ConnectTime = connectTime;
    }

    public long ConnectionId { get; init; }
    public string Host { get; init; }
    public int Port { get; init; }
    public bool UseTls { get; init; }
    public TimeSpan ConnectTime { get; init; }
}

/// <summary>
/// Represents the struct.
/// </summary>
public readonly record struct RedisConnectFailed
{
    public RedisConnectFailed(string host, int port, bool useTls, Exception exception)
    {
        Host = host;
        Port = port;
        UseTls = useTls;
        Exception = exception;
    }

    public string Host { get; init; }
    public int Port { get; init; }
    public bool UseTls { get; init; }
    public Exception Exception { get; init; }
}

/// <summary>
/// Represents the struct.
/// </summary>
public readonly record struct RedisAuthenticated
{
    public RedisAuthenticated(long connectionId, string? username, bool usedFallbackPasswordOnly)
    {
        ConnectionId = connectionId;
        Username = username;
        UsedFallbackPasswordOnly = usedFallbackPasswordOnly;
    }

    public long ConnectionId { get; init; }
    public string? Username { get; init; }
    public bool UsedFallbackPasswordOnly { get; init; }
}

/// <summary>
/// Represents the struct.
/// </summary>
public readonly record struct RedisDatabaseSelected
{
    public RedisDatabaseSelected(long connectionId, int database)
    {
        ConnectionId = connectionId;
        Database = database;
    }

    public long ConnectionId { get; init; }
    public int Database { get; init; }
}

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
