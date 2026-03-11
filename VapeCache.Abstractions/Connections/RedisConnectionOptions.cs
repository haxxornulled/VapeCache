namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Represents the redis connection options.
/// </summary>
public sealed record RedisConnectionOptions
{
    /// <summary>
    /// Gets or sets the host.
    /// </summary>
    public string Host { get; init; } = "";
    /// <summary>
    /// Gets or sets the port.
    /// </summary>
    public int Port { get; init; } = 6379;

    /// <summary>
    /// Gets or sets the username.
    /// </summary>
    public string? Username { get; init; }
    /// <summary>
    /// Gets or sets the password.
    /// </summary>
    public string? Password { get; init; }
    /// <summary>
    /// Gets or sets the database.
    /// </summary>
    public int Database { get; init; }

    // Optional: provide a full redis:// or rediss:// connection string.
    // This is ideal for KeyVault/secret stores. When set, parsed values override Host/Port/User/Password/Database/TLS settings.
    /// <summary>
    /// Gets or sets the connection string.
    /// </summary>
    public string? ConnectionString { get; init; }

    /// <summary>
    /// Gets or sets the use tls.
    /// </summary>
    public bool UseTls { get; init; }
    /// <summary>
    /// Gets or sets the tls host.
    /// </summary>
    public string? TlsHost { get; init; }

    /// <summary>
    /// WARNING: Development/testing ONLY. Allows bypassing TLS certificate validation.
    /// This creates a critical security vulnerability (MITM attacks) and MUST NOT be enabled in production.
    /// Consider using self-signed certs in dev or proper CA-signed certs instead.
    /// </summary>
    public bool AllowInvalidCert { get; init; }

    /// <summary>
    /// Gets or sets the max connections.
    /// </summary>
    public int MaxConnections { get; init; } = 64;
    /// <summary>
    /// Gets or sets the max idle.
    /// </summary>
    public int MaxIdle { get; init; } = 64;
    /// <summary>
    /// Gets or sets the warm.
    /// </summary>
    public int Warm { get; init; }

    /// <summary>
    /// Executes from seconds.
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>
    /// Executes from seconds.
    /// </summary>
    public TimeSpan AcquireTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Executes from seconds.
    /// </summary>
    public TimeSpan ValidateAfterIdle { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// Executes from milliseconds.
    /// </summary>
    public TimeSpan ValidateTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>
    /// Executes from minutes.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>
    /// Executes from hours.
    /// </summary>
    public TimeSpan MaxConnectionLifetime { get; init; } = TimeSpan.FromHours(1);
    /// <summary>
    /// Executes from seconds.
    /// </summary>
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

    /// <summary>
    /// Gets or sets the enable tcp keep alive.
    /// </summary>
    public bool EnableTcpKeepAlive { get; init; } = true;
    /// <summary>
    /// Executes from seconds.
    /// </summary>
    public TimeSpan TcpKeepAliveTime { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// Executes from seconds.
    /// </summary>
    public TimeSpan TcpKeepAliveInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the allow auth fallback to password only.
    /// </summary>
    public bool AllowAuthFallbackToPasswordOnly { get; init; }
    /// <summary>
    /// Gets or sets the log who am ion connect.
    /// </summary>
    public bool LogWhoAmIOnConnect { get; init; }

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

    /// <summary>
    /// RESP protocol version negotiated during connection setup. Supported values: 2 or 3.
    /// </summary>
    public int RespProtocolVersion { get; init; } = 2;

    /// <summary>
    /// Enables cluster redirect handling for MOVED/ASK responses on cache-path commands.
    /// </summary>
    public bool EnableClusterRedirection { get; init; }

    /// <summary>
    /// Maximum number of redirect hops allowed for a single command.
    /// </summary>
    public int MaxClusterRedirects { get; init; } = 3;
}
