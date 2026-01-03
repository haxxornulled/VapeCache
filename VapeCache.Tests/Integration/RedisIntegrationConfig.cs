using Microsoft.Extensions.Options;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Tests.Integration;

public static class RedisIntegrationConfig
{
    public static RedisConnectionOptions? TryLoad(out string? skipReason)
    {
        var host = GetEnv("VAPECACHE_REDIS_HOST");
        if (string.IsNullOrWhiteSpace(host))
        {
            skipReason = "Set VAPECACHE_REDIS_HOST (and optional VAPECACHE_REDIS_PORT, VAPECACHE_REDIS_USERNAME, VAPECACHE_REDIS_PASSWORD, VAPECACHE_REDIS_DATABASE, VAPECACHE_REDIS_USE_TLS, VAPECACHE_REDIS_TLS_HOST, VAPECACHE_REDIS_ALLOW_INVALID_CERT) to run Redis integration tests.";
            return null;
        }

        var port = TryGetInt("VAPECACHE_REDIS_PORT") ?? 6379;
        var username = GetEnv("VAPECACHE_REDIS_USERNAME");
        var password = GetEnv("VAPECACHE_REDIS_PASSWORD");
        var useTls = TryGetBool("VAPECACHE_REDIS_USE_TLS") ?? false;
        var tlsHost = GetEnv("VAPECACHE_REDIS_TLS_HOST");
        var allowInvalid = TryGetBool("VAPECACHE_REDIS_ALLOW_INVALID_CERT") ?? false;
        var database = TryGetInt("VAPECACHE_REDIS_DATABASE") ?? 0;

        var options = new RedisConnectionOptions
        {
            Host = host,
            Port = port,
            Username = string.IsNullOrWhiteSpace(username) ? null : username,
            Password = string.IsNullOrWhiteSpace(password) ? null : password,
            Database = database,
            UseTls = useTls,
            TlsHost = string.IsNullOrWhiteSpace(tlsHost) ? null : tlsHost,
            AllowInvalidCert = allowInvalid,
            MaxConnections = 4,
            MaxIdle = 4,
            Warm = 0,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AcquireTimeout = TimeSpan.FromSeconds(5)
        };

        if (!CanConnect(options, out skipReason))
            return null;

        skipReason = null;
        return options;
    }

    public static IOptionsMonitor<T> Monitor<T>(T value) where T : class => new OptionsMonitorStub<T>(value);

    private static int? TryGetInt(string key) =>
        int.TryParse(GetEnv(key), out var v) ? v : null;

    private static bool? TryGetBool(string key) =>
        bool.TryParse(GetEnv(key), out var v) ? v : null;

    private static string? GetEnv(string key)
    {
        var user = Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(user))
            return user;

        return Environment.GetEnvironmentVariable(key);
    }

    private static bool CanConnect(RedisConnectionOptions options, out string? skipReason)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            using var client = new TcpClient { NoDelay = true };
            client.ConnectAsync(options.Host, options.Port, cts.Token).GetAwaiter().GetResult();

            Stream stream = client.GetStream();
            if (options.UseTls)
            {
                var ssl = new SslStream(
                    stream,
                    leaveInnerStreamOpen: false,
                    options.AllowInvalidCert ? static (_, _, _, _) => true : null);

                ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = options.TlsHost ?? options.Host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, cts.Token).GetAwaiter().GetResult();

                stream = ssl;
            }

            using (stream)
            {
                if (!TryAuthenticate(stream, options, cts.Token, out var authError))
                {
                    skipReason = $"Redis AUTH failed for {options.Host}:{options.Port} ({authError}).";
                    return false;
                }

                if (options.Database != 0)
                {
                    var select = RedisResp.BuildCommand("SELECT", options.Database.ToString());
                    stream.WriteAsync(select, cts.Token).GetAwaiter().GetResult();
                    stream.FlushAsync(cts.Token).GetAwaiter().GetResult();
                    ExpectSimpleString(stream, "OK", cts.Token);
                }

                var ping = RedisResp.BuildCommand("PING");
                stream.WriteAsync(ping, cts.Token).GetAwaiter().GetResult();
                stream.FlushAsync(cts.Token).GetAwaiter().GetResult();
                ExpectSimpleString(stream, "PONG", cts.Token);
            }

            skipReason = null;
            return true;
        }
        catch (OperationCanceledException)
        {
            skipReason = $"Redis at {options.Host}:{options.Port} not reachable (connect timed out).";
            return false;
        }
        catch (Exception ex)
        {
            skipReason = $"Redis at {options.Host}:{options.Port} not reachable or not responding to PING ({ex.Message}).";
            return false;
        }
    }

    private static bool TryAuthenticate(Stream stream, RedisConnectionOptions options, CancellationToken ct, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(options.Password))
            return true;

        if (!string.IsNullOrEmpty(options.Username))
        {
            if (TryAuth(stream, options.Username, options.Password, ct, out error))
                return true;

            if (!options.AllowAuthFallbackToPasswordOnly)
                return false;
        }

        return TryAuth(stream, null, options.Password, ct, out error);
    }

    private static bool TryAuth(Stream stream, string? username, string password, CancellationToken ct, out string? error)
    {
        var auth = string.IsNullOrEmpty(username)
            ? RedisResp.BuildCommand("AUTH", password)
            : RedisResp.BuildCommand("AUTH", username, password);
        stream.WriteAsync(auth, ct).GetAwaiter().GetResult();
        stream.FlushAsync(ct).GetAwaiter().GetResult();

        if (TryExpectSimpleString(stream, "OK", ct, out var line))
        {
            error = null;
            return true;
        }

        error = line;
        return false;
    }

    private static void ExpectSimpleString(Stream stream, string expected, CancellationToken ct)
    {
        if (!TryExpectSimpleString(stream, expected, ct, out var line))
            throw new InvalidOperationException($"Expected +{expected}, got '{line}'.");
    }

    private static bool TryExpectSimpleString(Stream stream, string expected, CancellationToken ct, out string? line)
    {
        line = RedisResp.ReadLineAsync(stream, ct).GetAwaiter().GetResult();
        if (line.Length == 0 || line[0] != '+')
            return false;
        return string.Equals(line, "+" + expected, StringComparison.Ordinal);
    }

    private sealed class OptionsMonitorStub<T>(T value) : IOptionsMonitor<T> where T : class
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
