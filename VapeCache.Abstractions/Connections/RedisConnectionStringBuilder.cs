namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Represents the redis connection string builder.
/// </summary>
public sealed class RedisConnectionStringBuilder : IRedisConnectionStringBuilder
{
    /// <summary>
    /// Builds value.
    /// </summary>
    public string Build(RedisConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Host))
            throw new ArgumentException("Host is required.", nameof(options.Host));
        ArgumentOutOfRangeException.ThrowIfNegative(options.Database);
        if (options.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options.Port), "Port must be between 1 and 65535.");

        var sb = new System.Text.StringBuilder(128);
        sb.Append(options.UseTls ? "rediss://" : "redis://");

        if (!string.IsNullOrEmpty(options.Password))
        {
            if (!string.IsNullOrEmpty(options.Username))
            {
                sb.Append(Uri.EscapeDataString(options.Username));
                sb.Append(':').Append(Uri.EscapeDataString(options.Password)).Append('@');
            }
            else
            {
                sb.Append(':').Append(Uri.EscapeDataString(options.Password)).Append('@');
            }
        }

        sb.Append(options.Host).Append(':').Append(options.Port).Append('/').Append(options.Database);

        if (options.UseTls)
        {
            sb.Append("?tls=true");
            if (!string.IsNullOrEmpty(options.TlsHost))
                sb.Append("&sni=").Append(Uri.EscapeDataString(options.TlsHost));
            if (options.AllowInvalidCert)
                sb.Append("&allowInvalidCert=true");
        }

        return sb.ToString();
    }
}
