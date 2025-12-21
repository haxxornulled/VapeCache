namespace VapeCache.Application.Connections;

public sealed record RedisConnectionOptions
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 6379;

    public string? Username { get; init; }
    public string? Password { get; init; }
    public int Database { get; init; } = 0;

    // Optional: provide a full redis:// or rediss:// connection string.
    // This is ideal for KeyVault/secret stores. When set, parsed values override Host/Port/User/Password/Database/TLS settings.
    public string? ConnectionString { get; init; }

    public bool UseTls { get; init; }
    public string? TlsHost { get; init; }
    public bool AllowInvalidCert { get; init; }

    public int MaxConnections { get; init; } = 64;
    public int MaxIdle { get; init; } = 64;
    public int Warm { get; init; } = 0;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan AcquireTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan ValidateAfterIdle { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ValidateTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaxConnectionLifetime { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan ReaperPeriod { get; init; } = TimeSpan.FromSeconds(10);

    public bool EnableTcpKeepAlive { get; init; } = true;
    public TimeSpan TcpKeepAliveTime { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan TcpKeepAliveInterval { get; init; } = TimeSpan.FromSeconds(10);

    public bool AllowAuthFallbackToPasswordOnly { get; init; } = true;
    public bool LogWhoAmIOnConnect { get; init; } = false;
}
