using System.Globalization;

namespace VapeCache.Abstractions.Connections;

public static class RedisConnectionStringParser
{
    /// <summary>
    /// Attempts to value.
    /// </summary>
    public static bool TryParse(string? connectionString, out RedisConnectionOptions parsed, out string? error)
    {
        parsed = new RedisConnectionOptions();
        error = null;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            error = "Connection string is empty.";
            return false;
        }

        var text = connectionString.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            return TryParseEndpointStyle(text, out parsed, out error);

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            error = "Invalid URI.";
            return false;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        var useTls = scheme switch
        {
            "redis" => false,
            "rediss" => true,
            _ => (bool?)null
        };

        if (useTls is null)
        {
            error = $"Unsupported scheme '{uri.Scheme}'. Expected redis:// or rediss://.";
            return false;
        }

        var host = uri.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "Host is required.";
            return false;
        }

        var port = uri.IsDefaultPort ? 6379 : uri.Port;

        var username = (string?)null;
        var password = (string?)null;

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            if (parts.Length == 1)
            {
                // redis://:password@host
                password = Uri.UnescapeDataString(parts[0]);
            }
            else
            {
                username = Uri.UnescapeDataString(parts[0]);
                password = Uri.UnescapeDataString(parts[1]);
                if (username.Length == 0) username = null;
            }
        }

        var db = 0;
        var path = uri.AbsolutePath?.Trim('/');
        if (!string.IsNullOrEmpty(path))
        {
            if (!int.TryParse(path, NumberStyles.Integer, CultureInfo.InvariantCulture, out db) || db < 0)
            {
                error = $"Invalid database '{path}'.";
                return false;
            }
        }

        var tlsHost = (string?)null;
        var allowInvalidCert = false;

        if (!string.IsNullOrWhiteSpace(uri.Query))
        {
            // very small query parser: key=value&...
            var q = uri.Query.TrimStart('?');
            foreach (var segment in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = segment.Split('=', 2);
                var key = Uri.UnescapeDataString(kv[0]).Trim();
                var value = kv.Length == 2 ? Uri.UnescapeDataString(kv[1]).Trim() : "";

                if (key.Equals("sni", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("tlshost", StringComparison.OrdinalIgnoreCase))
                {
                    tlsHost = value.Length == 0 ? null : value;
                    continue;
                }

                if (key.Equals("allowInvalidCert", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("allowInvalidServerCertificate", StringComparison.OrdinalIgnoreCase))
                {
                    allowInvalidCert = ParseBool(value) ?? allowInvalidCert;
                    continue;
                }

                if (key.Equals("tls", StringComparison.OrdinalIgnoreCase))
                {
                    // If someone uses redis://...?...tls=true we respect it.
                    useTls = ParseBool(value) ?? useTls.Value;
                    continue;
                }
            }
        }

        parsed = new RedisConnectionOptions
        {
            Host = host,
            Port = port,
            Username = username,
            Password = password,
            Database = db,
            UseTls = useTls.Value,
            TlsHost = tlsHost,
            AllowInvalidCert = allowInvalidCert
        };

        return true;
    }

    private static bool TryParseEndpointStyle(string text, out RedisConnectionOptions parsed, out string? error)
    {
        parsed = new RedisConnectionOptions();
        error = null;

        var segments = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            error = "Connection string is empty.";
            return false;
        }

        var endpoint = segments[0];
        if (!TryParseEndpoint(endpoint, out var host, out var port))
        {
            error = "Invalid endpoint. Expected host[:port] as the first segment.";
            return false;
        }

        string? username = null;
        string? password = null;
        var database = 0;
        var useTls = false;
        string? tlsHost = null;
        var allowInvalidCert = false;

        for (var i = 1; i < segments.Length; i++)
        {
            var segment = segments[i];
            var kv = segment.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2)
                continue;

            var key = kv[0];
            var value = kv[1];

            if (key.Equals("user", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("username", StringComparison.OrdinalIgnoreCase))
            {
                username = value.Length == 0 ? null : value;
                continue;
            }

            if (key.Equals("password", StringComparison.OrdinalIgnoreCase))
            {
                password = value.Length == 0 ? null : value;
                continue;
            }

            if (key.Equals("defaultDatabase", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("db", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("database", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedDb) || parsedDb < 0)
                {
                    error = $"Invalid database '{value}'.";
                    return false;
                }
                database = parsedDb;
                continue;
            }

            if (key.Equals("ssl", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("tls", StringComparison.OrdinalIgnoreCase))
            {
                useTls = ParseBool(value) ?? useTls;
                continue;
            }

            if (key.Equals("sni", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("tlshost", StringComparison.OrdinalIgnoreCase))
            {
                tlsHost = value.Length == 0 ? null : value;
                continue;
            }

            if (key.Equals("allowInvalidCert", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("allowInvalidServerCertificate", StringComparison.OrdinalIgnoreCase))
            {
                allowInvalidCert = ParseBool(value) ?? allowInvalidCert;
                continue;
            }
        }

        parsed = new RedisConnectionOptions
        {
            Host = host,
            Port = port,
            Username = username,
            Password = password,
            Database = database,
            UseTls = useTls,
            TlsHost = tlsHost,
            AllowInvalidCert = allowInvalidCert
        };
        return true;
    }

    private static bool TryParseEndpoint(string endpoint, out string host, out int port)
    {
        host = string.Empty;
        port = 6379;

        var trimmed = endpoint.Trim();
        if (trimmed.Length == 0)
            return false;
        if (trimmed.Contains('='))
            return false;

        if (trimmed[0] == '[')
        {
            var endBracket = trimmed.IndexOf(']');
            if (endBracket < 0)
                return false;

            host = trimmed[1..endBracket];
            if (host.Length == 0)
                return false;

            if (endBracket + 1 == trimmed.Length)
                return true;

            if (endBracket + 2 > trimmed.Length || trimmed[endBracket + 1] != ':')
                return false;

            var portText = trimmed[(endBracket + 2)..];
            return int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) &&
                   port is > 0 and <= 65535;
        }

        var colonIndex = trimmed.LastIndexOf(':');
        if (colonIndex <= 0)
        {
            host = trimmed;
            return true;
        }

        var portTextSimple = trimmed[(colonIndex + 1)..];
        if (!int.TryParse(portTextSimple, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort) ||
            parsedPort is <= 0 or > 65535)
        {
            host = trimmed;
            return true;
        }

        host = trimmed[..colonIndex];
        port = parsedPort;
        return host.Length > 0;
    }

    private static bool? ParseBool(string value)
    {
        if (bool.TryParse(value, out var b)) return b;
        if (value == "1") return true;
        if (value == "0") return false;
        return null;
    }
}
