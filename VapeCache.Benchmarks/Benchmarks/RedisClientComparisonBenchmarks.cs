using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

[MemoryDiagnoser(displayGenColumns: true)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[Config(typeof(Config))]
[SimpleJob(warmupCount: 3, iterationCount: 12)]
public class RedisClientComparisonBenchmarks
{
    [Params(256, 2048)]
    public int PayloadBytes { get; set; }

    private byte[] _payload = Array.Empty<byte>();

    private RedisCommandExecutor? _ours;
    private ConnectionMultiplexer? _ser;
    private IDatabase? _db;

    private string _key = "";
    private string _hashKey = "";
    private string _listKey = "";
    private string _field = "f1";

    [GlobalSetup]
    public async Task Setup()
    {
        _payload = new byte[PayloadBytes];
        Random.Shared.NextBytes(_payload);

        var connStr = Environment.GetEnvironmentVariable("VAPECACHE_REDIS_CONNECTIONSTRING");
        if (string.IsNullOrWhiteSpace(connStr))
            throw new InvalidOperationException("Set VAPECACHE_REDIS_CONNECTIONSTRING to run Redis comparison benchmarks.");

        _key = "bench:cmp:" + Guid.NewGuid().ToString("N");
        _hashKey = _key + ":h";
        _listKey = _key + ":l";

        // Our client
        var redisOptions = new RedisConnectionOptions { ConnectionString = connStr };
        var factory = new RedisConnectionFactory(
            new SimpleOptionsMonitor(redisOptions),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());
        _ours = new RedisCommandExecutor(factory, Options.Create(new RedisMultiplexerOptions { Connections = 1, MaxInFlightPerConnection = 2048 }));

        // StackExchange.Redis
        if (!RedisConnectionStringParser.TryParse(connStr, out var parsed, out var error))
            throw new InvalidOperationException("Invalid connection string for benchmarks: " + error);

        var cfg = new ConfigurationOptions
        {
            AbortOnConnectFail = false,
            Ssl = parsed.UseTls,
            DefaultDatabase = parsed.Database,
            ConnectTimeout = 2000,
            SyncTimeout = 2000,
            AsyncTimeout = 2000
        };
        cfg.EndPoints.Add(parsed.Host, parsed.Port);
        if (!string.IsNullOrWhiteSpace(parsed.Username))
            cfg.User = parsed.Username;
        if (!string.IsNullOrWhiteSpace(parsed.Password))
            cfg.Password = parsed.Password;

        _ser = await ConnectionMultiplexer.ConnectAsync(cfg).ConfigureAwait(false);
        _db = _ser.GetDatabase(parsed.Database);

        // Warm keys
        await _db.StringSetAsync(_key, _payload).ConfigureAwait(false);
        await _db.HashSetAsync(_hashKey, _field, _payload).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_db is not null)
        {
            await _db.KeyDeleteAsync(_key).ConfigureAwait(false);
            await _db.KeyDeleteAsync(_hashKey).ConfigureAwait(false);
            await _db.KeyDeleteAsync(_listKey).ConfigureAwait(false);
        }

        if (_ours is not null)
            await _ours.DisposeAsync().ConfigureAwait(false);

        _ser?.Dispose();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("StringSetGet")]
    public async Task SER_StringSetGet()
    {
        var db = _db!;
        await db.StringSetAsync(_key, _payload).ConfigureAwait(false);
        _ = await db.StringGetAsync(_key).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("StringSetGet")]
    public async Task Ours_StringSetGet()
    {
        var ours = _ours!;
        _ = await ours.SetAsync(_key, _payload, ttl: null, CancellationToken.None).ConfigureAwait(false);
        using var lease = await ours.GetLeaseAsync(_key, CancellationToken.None).ConfigureAwait(false);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("HashSetGet")]
    public async Task SER_HashSetGet()
    {
        var db = _db!;
        await db.HashSetAsync(_hashKey, _field, _payload).ConfigureAwait(false);
        _ = await db.HashGetAsync(_hashKey, _field).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("HashSetGet")]
    public async Task Ours_HashSetGet()
    {
        var ours = _ours!;
        _ = await ours.HSetAsync(_hashKey, _field, _payload, CancellationToken.None).ConfigureAwait(false);
        using var lease = await ours.HGetLeaseAsync(_hashKey, _field, CancellationToken.None).ConfigureAwait(false);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("ListPushPop")]
    public async Task SER_ListPushPop()
    {
        var db = _db!;
        _ = await db.ListLeftPushAsync(_listKey, _payload).ConfigureAwait(false);
        _ = await db.ListLeftPopAsync(_listKey).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("ListPushPop")]
    public async Task Ours_ListPushPop()
    {
        var ours = _ours!;
        _ = await ours.LPushAsync(_listKey, _payload, CancellationToken.None).ConfigureAwait(false);
        using var lease = await ours.LPopLeaseAsync(_listKey, CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class SimpleOptionsMonitor : IOptionsMonitor<RedisConnectionOptions>
    {
        public SimpleOptionsMonitor(RedisConnectionOptions current) => CurrentValue = current;
        public RedisConnectionOptions CurrentValue { get; }
        public RedisConnectionOptions Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<RedisConnectionOptions, string?> listener) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class Config : ManualConfig
    {
        public Config()
        {
            AddExporter(MarkdownExporter.GitHub);
            AddExporter(HtmlExporter.Default);
            AddExporter(BenchmarkDotNet.Exporters.Csv.CsvExporter.Default);
            AddExporter(ComparisonMarkdownExporter.Default);

            AddColumn(StatisticColumn.Min);
            AddColumn(StatisticColumn.Max);
        }
    }
}
