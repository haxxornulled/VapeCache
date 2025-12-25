using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using VapeCache.Abstractions.Connections;
using VapeCache.Benchmarks;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

[Config(typeof(EnterpriseBenchmarkConfig))]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
public class SerVsOursEndToEndBenchmarks
{
    private const string Field = "f";

    [Params(32, 256, 4096)]
    public int PayloadBytes { get; set; }

    [Params(1)]
    public int Connections { get; set; }

    private readonly Consumer _consumer = new();

    private string _key = null!;
    private string _hashKey = null!;
    private string _listKey = null!;

    private byte[] _payload = null!;

    private IConnectionMultiplexer _serMux = null!;
    private IDatabase _serDb = null!;

    private RedisConnectionFactory _oursFactory = null!;
    private RedisCommandExecutor _ours = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var options = BenchmarkRedisConfig.Load();

        var prefix = $"bench:{Guid.NewGuid():N}";
        _key = $"{prefix}:str";
        _hashKey = $"{prefix}:hash";
        _listKey = $"{prefix}:list";

        _payload = GC.AllocateUninitializedArray<byte>(PayloadBytes);
        new Random(42).NextBytes(_payload);

        try
        {
            _serMux = await ConnectSerAsync(options).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to connect to Redis for end-to-end benchmarks (Host={options.Host} Port={options.Port} Db={options.Database} Tls={options.UseTls} User={(string.IsNullOrWhiteSpace(options.Username) ? "<none>" : options.Username)}). " +
                "Set VAPECACHE_REDIS_HOST (and optional VAPECACHE_REDIS_PORT, VAPECACHE_REDIS_USERNAME, VAPECACHE_REDIS_PASSWORD, VAPECACHE_REDIS_DATABASE, VAPECACHE_REDIS_USE_TLS, VAPECACHE_REDIS_TLS_HOST, VAPECACHE_REDIS_ALLOW_INVALID_CERT).",
                ex);
        }
        _serDb = _serMux.GetDatabase(options.Database);

        var monitor = new OptionsMonitorStub<RedisConnectionOptions>(options);
        _oursFactory = new RedisConnectionFactory(monitor, NullLogger<RedisConnectionFactory>.Instance, Array.Empty<IRedisConnectionObserver>());
        _ours = new RedisCommandExecutor(
            _oursFactory,
            Options.Create(new RedisMultiplexerOptions
            {
                Connections = Connections,
                MaxInFlightPerConnection = 4096,
                EnableCommandInstrumentation = false,
                EnableCoalescedSocketWrites = true
            }));

        // Clean any existing keys and seed for read benchmarks.
        await _serDb.KeyDeleteAsync(new RedisKey[] { _key, _hashKey, _listKey }).ConfigureAwait(false);

        await _serDb.StringSetAsync(_key, _payload).ConfigureAwait(false);
        await _serDb.HashSetAsync(_hashKey, Field, _payload).ConfigureAwait(false);

        // Warm up our transport path too (uses the same Redis server).
        await _ours.SetAsync(_key, _payload, ttl: null, CancellationToken.None).ConfigureAwait(false);
        await _ours.HSetAsync(_hashKey, Field, _payload, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<IConnectionMultiplexer> ConnectSerAsync(RedisConnectionOptions options)
    {
        // StackExchange.Redis can fail hard on initial connect by default; disable abort-on-connect-fail.
        // Also, some Redis servers don't support ACL usernames (Redis < 6), so retry with password-only auth when a username was provided.
        var attemptOptions = new List<RedisConnectionOptions>(2) { options };
        if (!string.IsNullOrWhiteSpace(options.Username))
            attemptOptions.Add(options with { Username = null });

        Exception? last = null;
        foreach (var attempt in attemptOptions)
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

            cfg.AbortOnConnectFail = false;

            try
            {
                var mux = await ConnectionMultiplexer.ConnectAsync(cfg).ConfigureAwait(false);

                // Ensure we are actually connected before running setup commands, otherwise first command can timeout.
                await mux.GetDatabase(attempt.Database).PingAsync().ConfigureAwait(false);
                return mux;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException("Failed to connect.");
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        try
        {
            if (_serDb is not null)
                await _serDb.KeyDeleteAsync(new RedisKey[] { _key, _hashKey, _listKey }).ConfigureAwait(false);
        }
        catch { }

        try { _serMux?.Dispose(); } catch { }
        if (_ours is not null) await _ours.DisposeAsync().ConfigureAwait(false);
    }

    // STRING

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("STRING_SET")]
    public async Task Ser_StringSet()
    {
        await _serDb.StringSetAsync(_key, _payload).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("STRING_SET")]
    public async Task Ours_StringSet()
    {
        var ok = await _ours.SetAsync(_key, _payload, ttl: null, CancellationToken.None).ConfigureAwait(false);
        _consumer.Consume(ok);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("STRING_GET")]
    public async Task Ser_StringGet()
    {
        var v = await _serDb.StringGetAsync(_key).ConfigureAwait(false);
        _consumer.Consume(v.HasValue);
    }

    [Benchmark]
    [BenchmarkCategory("STRING_GET")]
    public async Task Ours_StringGetLease()
    {
        using var lease = await _ours.GetLeaseAsync(_key, CancellationToken.None).ConfigureAwait(false);
        _consumer.Consume(lease.Length);
    }

    // HASH

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("HASH_HSET")]
    public async Task Ser_HashSet()
    {
        await _serDb.HashSetAsync(_hashKey, Field, _payload).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("HASH_HSET")]
    public async Task Ours_HashSet()
    {
        var n = await _ours.HSetAsync(_hashKey, Field, _payload, CancellationToken.None).ConfigureAwait(false);
        _consumer.Consume(n);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("HASH_HGET")]
    public async Task Ser_HashGet()
    {
        var v = await _serDb.HashGetAsync(_hashKey, Field).ConfigureAwait(false);
        _consumer.Consume(v.HasValue);
    }

    [Benchmark]
    [BenchmarkCategory("HASH_HGET")]
    public async Task Ours_HashGetLease()
    {
        using var lease = await _ours.HGetLeaseAsync(_hashKey, Field, CancellationToken.None).ConfigureAwait(false);
        _consumer.Consume(lease.Length);
    }

    // LIST

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("LIST_LPUSH")]
    public async Task Ser_ListPush()
    {
        await _serDb.ListLeftPushAsync(_listKey, _payload).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("LIST_LPUSH")]
    public async Task Ours_ListPush()
    {
        var n = await _ours.LPushAsync(_listKey, _payload, CancellationToken.None).ConfigureAwait(false);
        _consumer.Consume(n);
    }

    [IterationSetup(Target = nameof(Ser_ListPop))]
    public void Ser_ListPop_SetupAsync()
    {
        _serDb.KeyDeleteAsync(_listKey).GetAwaiter().GetResult();
        _serDb.ListLeftPushAsync(_listKey, _payload).GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("LIST_LPOP")]
    public async Task Ser_ListPop()
    {
        var v = await _serDb.ListLeftPopAsync(_listKey).ConfigureAwait(false);
        _consumer.Consume(v.HasValue);
    }

    [IterationSetup(Target = nameof(Ours_ListPopLease))]
    public void Ours_ListPop_SetupAsync()
    {
        _serDb.KeyDeleteAsync(_listKey).GetAwaiter().GetResult();
        _ours.LPushAsync(_listKey, _payload, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Benchmark]
    [BenchmarkCategory("LIST_LPOP")]
    public async Task Ours_ListPopLease()
    {
        using var lease = await _ours.LPopLeaseAsync(_listKey, CancellationToken.None).ConfigureAwait(false);
        _consumer.Consume(lease.Length);
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

    private static class BenchmarkRedisConfig
    {
        public static RedisConnectionOptions Load()
        {
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

        private static int? TryGetInt(string key) =>
            int.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : null;

        private static bool? TryGetBool(string key) =>
            bool.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : null;
    }
}
