using System.Globalization;
using System.Text;

namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Represents the redis connection string builder.
/// </summary>
public sealed class RedisConnectionStringBuilder : IRedisConnectionStringBuilder
{
    private const int DefaultRedisPort = 6379;

    /// <summary>
    /// Builds value.
    /// </summary>
    public string Build(RedisConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var host = NormalizeHost(options.Host);
        ArgumentOutOfRangeException.ThrowIfNegative(options.Database);
        if (options.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options), "options.Port must be between 1 and 65535.");

        if (options.AllowInvalidCert && !options.UseTls)
            throw new ArgumentException("AllowInvalidCert requires UseTls=true.", nameof(options));
        if (!string.IsNullOrWhiteSpace(options.TlsHost) && !options.UseTls)
            throw new ArgumentException("TlsHost requires UseTls=true.", nameof(options));

        var hasPassword = !string.IsNullOrEmpty(options.Password);
        var hasUsername = !string.IsNullOrWhiteSpace(options.Username);
        if (hasUsername && !hasPassword)
            throw new ArgumentException("Username requires a non-empty Password.", nameof(options));

        var sb = new StringBuilder(128);
        sb.Append(options.UseTls ? "rediss://" : "redis://");

        if (hasPassword)
        {
            if (hasUsername)
            {
                sb.Append(Uri.EscapeDataString(options.Username!.Trim()));
                sb.Append(':').Append(Uri.EscapeDataString(options.Password!)).Append('@');
            }
            else
            {
                sb.Append(':').Append(Uri.EscapeDataString(options.Password!)).Append('@');
            }
        }

        sb.Append(host)
            .Append(':')
            .Append(options.Port.ToString(CultureInfo.InvariantCulture))
            .Append('/')
            .Append(options.Database.ToString(CultureInfo.InvariantCulture));

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

    private static string NormalizeHost(string? rawHost)
    {
        if (string.IsNullOrWhiteSpace(rawHost))
            throw new ArgumentException("options.Host is required.", nameof(rawHost));

        var host = rawHost.Trim();
        if (host.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("Host should not include a URI scheme.", nameof(rawHost));
        if (host.IndexOfAny(['/', '?', '#']) >= 0)
            throw new ArgumentException("Host contains invalid URI path/query/fragment characters.", nameof(rawHost));

        if (host[0] == '[' || host[^1] == ']')
        {
            if (!(host[0] == '[' && host[^1] == ']'))
                throw new ArgumentException("Host has malformed IPv6 brackets.", nameof(rawHost));
            if (host.Length <= 2)
                throw new ArgumentException("Host is empty.", nameof(rawHost));

            return host;
        }

        var firstColon = host.IndexOf(':');
        var lastColon = host.LastIndexOf(':');

        if (firstColon >= 0 && firstColon == lastColon)
        {
            var maybePort = host[(lastColon + 1)..];
            if (int.TryParse(maybePort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort) &&
                parsedPort is > 0 and <= 65535)
            {
                throw new ArgumentException(
                    $"Host should not include a port. Use options.Port (default {DefaultRedisPort}).",
                    nameof(rawHost));
            }
        }

        if (firstColon >= 0)
            return "[" + host + "]";

        return host;
    }
}
