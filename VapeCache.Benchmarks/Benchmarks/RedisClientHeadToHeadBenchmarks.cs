using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using StackExchange.Redis;
using VapeCache.Benchmarks;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

[MemoryDiagnoser(displayGenColumns: true)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(warmupCount: 3, iterationCount: 12)]
public class RedisClientHeadToHeadBenchmarks
{
    private static readonly RedisClientOperation[] FullOperations =
    [
        RedisClientOperation.StringSetGet,
        RedisClientOperation.HashSetGet,
        RedisClientOperation.ListPushPop,
        RedisClientOperation.Ping,
        RedisClientOperation.ModuleList
    ];

    private static readonly RedisClientOperation[] QuickOperations =
    [
        RedisClientOperation.StringSetGet,
        RedisClientOperation.HashSetGet,
        RedisClientOperation.ListPushPop
    ];

    private static readonly int[] FullPayloadSizes = [256, 1024, 4096, 16384];
    private static readonly int[] QuickPayloadSizes = [256, 4096];
    private static readonly bool[] FullInstrumentationModes = [false, true];
    private static readonly bool[] QuickInstrumentationModes = [false];
    private static readonly VapeReadPath[] FullReadPaths = [VapeReadPath.Lease, VapeReadPath.Materialized];
    private static readonly VapeReadPath[] QuickReadPaths = [VapeReadPath.Lease];

    [ParamsSource(nameof(Operations))]
    public RedisClientOperation Operation { get; set; }

    [ParamsSource(nameof(PayloadSizes))]
    public int PayloadBytes { get; set; }

    [ParamsSource(nameof(InstrumentationModes))]
    public bool EnableInstrumentation { get; set; }

    [ParamsSource(nameof(ReadPaths))]
    public VapeReadPath ReadPath { get; set; }

    public IEnumerable<RedisClientOperation> Operations =>
        BenchmarkRedisConfig.ResolveEnumParams("VAPECACHE_BENCH_CLIENT_OPERATIONS", FullOperations, QuickOperations);

    public IEnumerable<int> PayloadSizes =>
        BenchmarkRedisConfig.ResolveIntParamsWithFallback(
            "VAPECACHE_BENCH_CLIENT_PAYLOADS",
            "VAPECACHE_BENCH_PARITY_PAYLOADS",
            FullPayloadSizes,
            QuickPayloadSizes);

    public IEnumerable<bool> InstrumentationModes =>
        BenchmarkRedisConfig.ResolveBoolParams(
            "VAPECACHE_BENCH_CLIENT_INSTRUMENTATION",
            FullInstrumentationModes,
            QuickInstrumentationModes);

    public IEnumerable<VapeReadPath> ReadPaths =>
        BenchmarkRedisConfig.ResolveEnumParams(
            "VAPECACHE_BENCH_CLIENT_READ_PATHS",
            FullReadPaths,
            QuickReadPaths);

    private readonly Consumer _consumer = new();
    private byte[] _payload = Array.Empty<byte>();

    private ConnectionMultiplexer? _ser;
    private IDatabase? _db;
    private RedisCommandExecutor? _executor;

    private string _key = "";
    private string _hashKey = "";
    private string _listKey = "";
    private string _field = "f1";

    [GlobalSetup]
    public async Task Setup()
    {
        _payload = new byte[PayloadBytes];
        BenchmarkRedisConfig.FillPayload(_payload, seed: 1000 + PayloadBytes);

        var options = BenchmarkRedisConfig.Load();

        _key = "bench:ser:" + Guid.NewGuid().ToString("N");
        _hashKey = _key + ":h";
        _listKey = _key + ":l";

        _ser = await BenchmarkRedisConfig.ConnectStackExchangeAsync(options).ConfigureAwait(false);
        _db = _ser.GetDatabase(options.Database);
        _executor = BenchmarkRedisConfig.CreateVapeCacheExecutor(options, enableInstrumentation: EnableInstrumentation);

        await _db.StringSetAsync(_key, _payload).ConfigureAwait(false);
        await _db.HashSetAsync(_hashKey, _field, _payload).ConfigureAwait(false);
        await _executor.SetAsync(_key, _payload, ttl: null, CancellationToken.None).ConfigureAwait(false);
        await _executor.HSetAsync(_hashKey, _field, _payload, CancellationToken.None).ConfigureAwait(false);
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

        if (_executor is not null)
        {
            await _executor.DeleteAsync(_key, CancellationToken.None).ConfigureAwait(false);
            await _executor.DeleteAsync(_hashKey, CancellationToken.None).ConfigureAwait(false);
            await _executor.DeleteAsync(_listKey, CancellationToken.None).ConfigureAwait(false);
            await _executor.DisposeAsync().ConfigureAwait(false);
        }

        _ser?.Dispose();
    }

    [BenchmarkCategory("RedisClientHeadToHead")]
    [Benchmark(Baseline = true)]
    public async Task StackExchange()
    {
        switch (Operation)
        {
            case RedisClientOperation.StringSetGet:
                await _db!.StringSetAsync(_key, _payload).ConfigureAwait(false);
                _consumer.Consume((await _db.StringGetAsync(_key).ConfigureAwait(false)).HasValue);
                break;
            case RedisClientOperation.HashSetGet:
                await _db!.HashSetAsync(_hashKey, _field, _payload).ConfigureAwait(false);
                _consumer.Consume((await _db.HashGetAsync(_hashKey, _field).ConfigureAwait(false)).HasValue);
                break;
            case RedisClientOperation.ListPushPop:
                _consumer.Consume(await _db!.ListLeftPushAsync(_listKey, _payload).ConfigureAwait(false));
                _consumer.Consume((await _db.ListLeftPopAsync(_listKey).ConfigureAwait(false)).HasValue);
                break;
            case RedisClientOperation.Ping:
                _consumer.Consume(await _db!.ExecuteAsync("PING").ConfigureAwait(false));
                break;
            case RedisClientOperation.ModuleList:
                _consumer.Consume(await _db!.ExecuteAsync("MODULE", "LIST").ConfigureAwait(false));
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    [Benchmark]
    [BenchmarkCategory("RedisClientHeadToHead")]
    public async Task VapeCache()
    {
        switch (Operation)
        {
            case RedisClientOperation.StringSetGet:
                _consumer.Consume(await _executor!.SetAsync(_key, _payload, ttl: null, CancellationToken.None).ConfigureAwait(false));
                if (ReadPath == VapeReadPath.Lease)
                {
                    using var lease = await _executor.GetLeaseAsync(_key, CancellationToken.None).ConfigureAwait(false);
                    _consumer.Consume(lease.Length);
                }
                else
                {
                    var bytes = await _executor.GetAsync(_key, CancellationToken.None).ConfigureAwait(false);
                    _consumer.Consume(bytes?.Length ?? -1);
                }
                break;
            case RedisClientOperation.HashSetGet:
                _consumer.Consume(await _executor!.HSetAsync(_hashKey, _field, _payload, CancellationToken.None).ConfigureAwait(false));
                if (ReadPath == VapeReadPath.Lease)
                {
                    using var lease = await _executor.HGetLeaseAsync(_hashKey, _field, CancellationToken.None).ConfigureAwait(false);
                    _consumer.Consume(lease.Length);
                }
                else
                {
                    var bytes = await _executor.HGetAsync(_hashKey, _field, CancellationToken.None).ConfigureAwait(false);
                    _consumer.Consume(bytes?.Length ?? -1);
                }
                break;
            case RedisClientOperation.ListPushPop:
                _consumer.Consume(await _executor!.LPushAsync(_listKey, _payload, CancellationToken.None).ConfigureAwait(false));
                if (ReadPath == VapeReadPath.Lease)
                {
                    using var lease = await _executor.LPopLeaseAsync(_listKey, CancellationToken.None).ConfigureAwait(false);
                    _consumer.Consume(lease.Length);
                }
                else
                {
                    var bytes = await _executor.LPopAsync(_listKey, CancellationToken.None).ConfigureAwait(false);
                    _consumer.Consume(bytes?.Length ?? -1);
                }
                break;
            case RedisClientOperation.Ping:
                _consumer.Consume(await _executor!.PingAsync(CancellationToken.None).ConfigureAwait(false));
                break;
            case RedisClientOperation.ModuleList:
                _consumer.Consume((await _executor!.ModuleListAsync(CancellationToken.None).ConfigureAwait(false)).Length);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    public enum RedisClientOperation
    {
        StringSetGet,
        HashSetGet,
        ListPushPop,
        Ping,
        ModuleList
    }

    public enum VapeReadPath
    {
        Lease,
        Materialized
    }
}
