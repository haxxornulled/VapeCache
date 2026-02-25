namespace VapeCache.Abstractions.Connections;

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

    /// <summary>
    /// WARNING: Development/testing ONLY. Allows bypassing TLS certificate validation.
    /// This creates a critical security vulnerability (MITM attacks) and MUST NOT be enabled in production.
    /// Consider using self-signed certs in dev or proper CA-signed certs instead.
    /// </summary>
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

    /// <summary>
    /// Named transport profile. Set to Custom to use explicitly configured transport values.
    /// </summary>
    public RedisTransportProfile TransportProfile { get; init; } = RedisTransportProfile.FullTilt;

    /// <summary>
    /// Controls Nagle's algorithm. True favors lower latency for request/response workloads.
    /// </summary>
    public bool EnableTcpNoDelay { get; init; } = true;

    /// <summary>
    /// Socket send buffer size in bytes. Defaults to a full-tilt profile (4MB) and can be tuned down.
    /// Set to 0 to use OS defaults/autotuning.
    /// </summary>
    public int TcpSendBufferBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>
    /// Socket receive buffer size in bytes. Defaults to a full-tilt profile (4MB) and can be tuned down.
    /// Set to 0 to use OS defaults/autotuning.
    /// </summary>
    public int TcpReceiveBufferBytes { get; init; } = 4 * 1024 * 1024;

    public bool EnableTcpKeepAlive { get; init; } = true;
    public TimeSpan TcpKeepAliveTime { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan TcpKeepAliveInterval { get; init; } = TimeSpan.FromSeconds(10);

    public bool AllowAuthFallbackToPasswordOnly { get; init; } = true;
    public bool LogWhoAmIOnConnect { get; init; } = false;

    /// <summary>
    /// Maximum allowed size for Redis bulk strings (RESP protocol).
    /// Prevents DoS attacks where malicious Redis server sends extremely large bulk strings.
    /// Default: 16MB. Set to -1 for unlimited (not recommended for production).
    /// </summary>
    public int MaxBulkStringBytes { get; init; } = 16 * 1024 * 1024; // 16MB

    /// <summary>
    /// Maximum nesting depth for Redis arrays (RESP protocol).
    /// Prevents stack overflow from pathological deeply-nested array responses.
    /// Default: 64 levels. Set to -1 for unlimited (not recommended).
    /// </summary>
    public int MaxArrayDepth { get; init; } = 64;
}
