using System.Globalization;

namespace VapeCache.Abstractions.Connections;

public static class RedisConnectionStringParser
{
    public static bool TryParse(string? connectionString, out RedisConnectionOptions parsed, out string? error)
    {
        parsed = new RedisConnectionOptions();
        error = null;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            error = "Connection string is empty.";
            return false;
        }

        if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
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

    private static bool? ParseBool(string value)
    {
        if (bool.TryParse(value, out var b)) return b;
        if (value == "1") return true;
        if (value == "0") return false;
        return null;
    }
}
