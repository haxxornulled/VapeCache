using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks;

internal static class BenchmarkRedisConfig
{
    public static bool IsQuickMode() => TryGetBool("VAPECACHE_BENCH_QUICK") ?? false;

    public static bool UseTextPayload() => TryGetBool("VAPECACHE_BENCH_TEXT_PAYLOAD") ?? false;

    public static int[] ResolveIntParams(string key, IReadOnlyList<int> fullDefaults, IReadOnlyList<int> quickDefaults)
    {
        var parsed = ParseCsv(Environment.GetEnvironmentVariable(key))
            .Select(token => int.TryParse(token, out var value) ? value : (int?)null)
            .Where(value => value.HasValue && value.Value > 0)
            .Select(value => value!.Value)
            .Distinct()
            .ToArray();

        if (parsed.Length > 0)
            return parsed;

        return (IsQuickMode() ? quickDefaults : fullDefaults).ToArray();
    }

    public static TEnum[] ResolveEnumParams<TEnum>(string key, IReadOnlyList<TEnum> fullDefaults, IReadOnlyList<TEnum> quickDefaults)
        where TEnum : struct, Enum
    {
        var parsed = ParseCsv(Environment.GetEnvironmentVariable(key))
            .Select(token => Enum.TryParse<TEnum>(token, ignoreCase: true, out var value) ? value : (TEnum?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Distinct()
            .ToArray();

        if (parsed.Length > 0)
            return parsed;

        return (IsQuickMode() ? quickDefaults : fullDefaults).ToArray();
    }

    public static bool[] ResolveBoolParams(string key, IReadOnlyList<bool> fullDefaults, IReadOnlyList<bool> quickDefaults)
    {
        var parsed = ParseCsv(Environment.GetEnvironmentVariable(key))
            .Select(token => bool.TryParse(token, out var value) ? value : (bool?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Distinct()
            .ToArray();

        if (parsed.Length > 0)
            return parsed;

        return (IsQuickMode() ? quickDefaults : fullDefaults).ToArray();
    }

    public static void FillPayload(Span<byte> buffer, int seed = 42)
    {
        if (buffer.IsEmpty)
            return;

        if (UseTextPayload())
        {
            // Deterministic printable payload for tools like RedisInsight.
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
            for (var i = 0; i < buffer.Length; i++)
                buffer[i] = (byte)alphabet[i % alphabet.Length];
            return;
        }

        var random = new Random(seed);
        random.NextBytes(buffer);
    }

    public static RedisConnectionOptions Load()
    {
        var connectionString = Environment.GetEnvironmentVariable("VAPECACHE_REDIS_CONNECTIONSTRING");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            if (!RedisConnectionStringParser.TryParse(connectionString, out var parsed, out var error))
                throw new InvalidOperationException("Invalid VAPECACHE_REDIS_CONNECTIONSTRING: " + error);

            return parsed with { ConnectionString = connectionString };
        }

        var host = Environment.GetEnvironmentVariable("VAPECACHE_REDIS_HOST");
        host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;

        var port = TryGetInt("VAPECACHE_REDIS_PORT") ?? 6379;
        var username = Environment.GetEnvironmentVariable("VAPECACHE_REDIS_USERNAME");
        var password = Environment.GetEnvironmentVariable("VAPECACHE_REDIS_PASSWORD");
        var useTls = TryGetBool("VAPECACHE_REDIS_USE_TLS") ?? false;
        var tlsHost = Environment.GetEnvironmentVariable("VAPECACHE_REDIS_TLS_HOST");
        var allowInvalid = TryGetBool("VAPECACHE_REDIS_ALLOW_INVALID_CERT") ?? false;
        var database = TryGetInt("VAPECACHE_REDIS_DATABASE") ?? 0;

        return new RedisConnectionOptions
        {
            Host = host,
            Port = port,
            Username = string.IsNullOrWhiteSpace(username) ? null : username,
            Password = password,
            Database = database,
            UseTls = useTls,
            TlsHost = string.IsNullOrWhiteSpace(tlsHost) ? null : tlsHost,
            AllowInvalidCert = allowInvalid
        };
    }

    public static RedisCommandExecutor CreateVapeCacheExecutor(
        RedisConnectionOptions options,
        int connections = 1,
        int maxInFlight = 2048,
        bool enableInstrumentation = true,
        bool enableCoalescedWrites = true,
        bool useDedicatedLaneWorkers = false,
        bool enableSocketRespReader = false)
    {
        var envInstrument = TryGetBool("VAPECACHE_BENCH_INSTRUMENT");
        if (envInstrument.HasValue)
            enableInstrumentation = envInstrument.Value;

        var envCoalescedWrites = TryGetBool("VAPECACHE_BENCH_COALESCED_WRITES");
        if (envCoalescedWrites.HasValue)
            enableCoalescedWrites = envCoalescedWrites.Value;

        var envDedicatedWorkers = TryGetBool("VAPECACHE_BENCH_DEDICATED_LANE_WORKERS");
        if (envDedicatedWorkers.HasValue)
            useDedicatedLaneWorkers = envDedicatedWorkers.Value;

        var envSocketRespReader = TryGetBool("VAPECACHE_BENCH_SOCKET_RESP_READER");
        if (envSocketRespReader.HasValue)
            enableSocketRespReader = envSocketRespReader.Value;

        var monitor = new SimpleOptionsMonitor(options);
        var factory = new RedisConnectionFactory(
            monitor,
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        return new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions
            {
                Connections = connections,
                MaxInFlightPerConnection = maxInFlight,
                EnableCommandInstrumentation = enableInstrumentation,
                EnableCoalescedSocketWrites = enableCoalescedWrites,
                UseDedicatedLaneWorkers = useDedicatedLaneWorkers,
                EnableSocketRespReader = enableSocketRespReader
            }));
    }

    public static async Task<ConnectionMultiplexer> ConnectStackExchangeAsync(RedisConnectionOptions options)
    {
        var attempts = new List<RedisConnectionOptions>(2) { options };
        if (!string.IsNullOrWhiteSpace(options.Username))
            attempts.Add(options with { Username = null });

        Exception? last = null;
        foreach (var attempt in attempts)
        {
            var cfg = new ConfigurationOptions
            {
                AbortOnConnectFail = false,
                ConnectRetry = 5,
                ConnectTimeout = (int)Math.Max(5_000, attempt.ConnectTimeout.TotalMilliseconds),
                SyncTimeout = 15_000,
                AsyncTimeout = 15_000,
                DefaultDatabase = attempt.Database,
                Ssl = attempt.UseTls,
                SslHost = attempt.UseTls ? (attempt.TlsHost ?? attempt.Host) : null,
                User = string.IsNullOrWhiteSpace(attempt.Username) ? null : attempt.Username,
                Password = attempt.Password,
                IncludeDetailInExceptions = false
            };
            cfg.EndPoints.Add(attempt.Host, attempt.Port);

            try
            {
                var mux = await ConnectionMultiplexer.ConnectAsync(cfg).ConfigureAwait(false);
                await mux.GetDatabase(attempt.Database).PingAsync().ConfigureAwait(false);
                return mux;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException("Failed to connect to Redis for benchmarks.", last);
    }

    private static int? TryGetInt(string key) =>
        int.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : null;

    private static bool? TryGetBool(string key) =>
        bool.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : null;

    private static IEnumerable<string> ParseCsv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        foreach (var token in value.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(token))
                yield return token;
        }
    }

    private sealed class SimpleOptionsMonitor : IOptionsMonitor<RedisConnectionOptions>
    {
        public SimpleOptionsMonitor(RedisConnectionOptions current) => CurrentValue = current;
        public RedisConnectionOptions CurrentValue { get; }
        public RedisConnectionOptions Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<RedisConnectionOptions, string?> listener) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
}
