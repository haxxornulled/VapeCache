using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Tests.Integration;

public static class RedisIntegrationConfig
{
    public static RedisConnectionOptions? TryLoad(out string? skipReason)
    {
        var host = Environment.GetEnvironmentVariable("VAPECACHE_REDIS_HOST");
        if (string.IsNullOrWhiteSpace(host))
        {
            skipReason = "Set VAPECACHE_REDIS_HOST (and optional VAPECACHE_REDIS_PORT, VAPECACHE_REDIS_USERNAME, VAPECACHE_REDIS_PASSWORD, VAPECACHE_REDIS_DATABASE, VAPECACHE_REDIS_USE_TLS, VAPECACHE_REDIS_TLS_HOST, VAPECACHE_REDIS_ALLOW_INVALID_CERT) to run Redis integration tests.";
            return null;
        }

        var port = TryGetInt("VAPECACHE_REDIS_PORT") ?? 6379;
        var username = Environment.GetEnvironmentVariable("VAPECACHE_REDIS_USERNAME");
        var password = Environment.GetEnvironmentVariable("VAPECACHE_REDIS_PASSWORD");
        var useTls = TryGetBool("VAPECACHE_REDIS_USE_TLS") ?? false;
        var tlsHost = Environment.GetEnvironmentVariable("VAPECACHE_REDIS_TLS_HOST");
        var allowInvalid = TryGetBool("VAPECACHE_REDIS_ALLOW_INVALID_CERT") ?? false;
        var database = TryGetInt("VAPECACHE_REDIS_DATABASE") ?? 0;

        skipReason = null;
        return new RedisConnectionOptions
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
    }

    public static IOptionsMonitor<T> Monitor<T>(T value) where T : class => new OptionsMonitorStub<T>(value);

    private static int? TryGetInt(string key) =>
        int.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : null;

    private static bool? TryGetBool(string key) =>
        bool.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : null;

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
