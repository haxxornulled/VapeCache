using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using StackExchange.Redis;
using VapeCache.Benchmarks;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

[Config(typeof(EnterpriseBenchmarkConfig))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class RedisEndToEndHeadToHeadBenchmarks
{
    private const string Field = "f";

    private static readonly RedisEndToEndOperation[] FullOperations =
    [
        RedisEndToEndOperation.StringSet,
        RedisEndToEndOperation.StringGet,
        RedisEndToEndOperation.HashSet,
        RedisEndToEndOperation.HashGet,
        RedisEndToEndOperation.ListPush,
        RedisEndToEndOperation.ListPop
    ];

    private static readonly RedisEndToEndOperation[] QuickOperations =
    [
        RedisEndToEndOperation.StringSet,
        RedisEndToEndOperation.StringGet,
        RedisEndToEndOperation.HashSet,
        RedisEndToEndOperation.HashGet
    ];

    private static readonly int[] FullPayloadSizes = [256, 1024, 4096, 16384];
    private static readonly int[] QuickPayloadSizes = [256, 4096];
    private static readonly bool[] FullInstrumentationModes = [false, true];
    private static readonly bool[] QuickInstrumentationModes = [false];
    private static readonly VapeReadPath[] FullReadPaths = [VapeReadPath.Lease, VapeReadPath.Materialized];
    private static readonly VapeReadPath[] QuickReadPaths = [VapeReadPath.Lease];

    [ParamsSource(nameof(Operations))]
    public RedisEndToEndOperation Operation { get; set; }

    [ParamsSource(nameof(PayloadSizes))]
    public int PayloadBytes { get; set; }

    [ParamsSource(nameof(InstrumentationModes))]
    public bool EnableInstrumentation { get; set; }

    [ParamsSource(nameof(ReadPaths))]
    public VapeReadPath ReadPath { get; set; }

    public IEnumerable<RedisEndToEndOperation> Operations =>
        BenchmarkRedisConfig.ResolveEnumParams("VAPECACHE_BENCH_E2E_OPERATIONS", FullOperations, QuickOperations);

    public IEnumerable<int> PayloadSizes =>
        BenchmarkRedisConfig.ResolveIntParamsWithFallback(
            "VAPECACHE_BENCH_E2E_PAYLOADS",
            "VAPECACHE_BENCH_PARITY_PAYLOADS",
            FullPayloadSizes,
            QuickPayloadSizes);

    public IEnumerable<bool> InstrumentationModes =>
        BenchmarkRedisConfig.ResolveBoolParams(
            "VAPECACHE_BENCH_E2E_INSTRUMENTATION",
            FullInstrumentationModes,
            QuickInstrumentationModes);

    public IEnumerable<VapeReadPath> ReadPaths =>
        BenchmarkRedisConfig.ResolveEnumParams(
            "VAPECACHE_BENCH_E2E_READ_PATHS",
            FullReadPaths,
            QuickReadPaths);

    private readonly Consumer _consumer = new();

    private string _key = null!;
    private string _hashKey = null!;
    private string _listKey = null!;

    private byte[] _payload = null!;

    private ConnectionMultiplexer _serMux = null!;
    private IDatabase _serDb = null!;
    private RedisCommandExecutor _executor = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var options = BenchmarkRedisConfig.Load();

        var prefix = $"bench:ser:{Guid.NewGuid():N}";
        _key = $"{prefix}:str";
        _hashKey = $"{prefix}:hash";
        _listKey = $"{prefix}:list";

        _payload = GC.AllocateUninitializedArray<byte>(PayloadBytes);
        BenchmarkRedisConfig.FillPayload(_payload, seed: 2000 + PayloadBytes);

        _serMux = await BenchmarkRedisConfig.ConnectStackExchangeAsync(options).ConfigureAwait(false);
        _serDb = _serMux.GetDatabase(options.Database);
        _executor = BenchmarkRedisConfig.CreateVapeCacheExecutor(
            options,
            connections: 1,
            maxInFlight: 4096,
            enableInstrumentation: EnableInstrumentation,
            enableCoalescedWrites: true);

        await _serDb.KeyDeleteAsync(new RedisKey[] { _key, _hashKey, _listKey }).ConfigureAwait(false);
        await _serDb.StringSetAsync(_key, _payload).ConfigureAwait(false);
        await _serDb.HashSetAsync(_hashKey, Field, _payload).ConfigureAwait(false);

        await _executor.DeleteAsync(_key, CancellationToken.None).ConfigureAwait(false);
        await _executor.DeleteAsync(_hashKey, CancellationToken.None).ConfigureAwait(false);
        await _executor.DeleteAsync(_listKey, CancellationToken.None).ConfigureAwait(false);
        await _executor.SetAsync(_key, _payload, ttl: null, CancellationToken.None).ConfigureAwait(false);
        await _executor.HSetAsync(_hashKey, Field, _payload, CancellationToken.None).ConfigureAwait(false);
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

        try
        {
            if (_executor is not null)
            {
                await _executor.DeleteAsync(_key, CancellationToken.None).ConfigureAwait(false);
                await _executor.DeleteAsync(_hashKey, CancellationToken.None).ConfigureAwait(false);
                await _executor.DeleteAsync(_listKey, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch { }

        if (_executor is not null)
            await _executor.DisposeAsync().ConfigureAwait(false);
        try { _serMux?.Dispose(); } catch { }
    }

    [BenchmarkCategory("RedisEndToEndHeadToHead")]
    [Benchmark(Baseline = true)]
    public async Task StackExchange()
    {
        switch (Operation)
        {
            case RedisEndToEndOperation.StringSet:
                _consumer.Consume(await _serDb.StringSetAsync(_key, _payload).ConfigureAwait(false));
                break;
            case RedisEndToEndOperation.StringGet:
                _consumer.Consume((await _serDb.StringGetAsync(_key).ConfigureAwait(false)).HasValue);
                break;
            case RedisEndToEndOperation.HashSet:
                _consumer.Consume(await _serDb.HashSetAsync(_hashKey, Field, _payload).ConfigureAwait(false));
                break;
            case RedisEndToEndOperation.HashGet:
                _consumer.Consume((await _serDb.HashGetAsync(_hashKey, Field).ConfigureAwait(false)).HasValue);
                break;
            case RedisEndToEndOperation.ListPush:
                _consumer.Consume(await _serDb.ListLeftPushAsync(_listKey, _payload).ConfigureAwait(false));
                break;
            case RedisEndToEndOperation.ListPop:
                await _serDb.KeyDeleteAsync(_listKey).ConfigureAwait(false);
                _consumer.Consume(await _serDb.ListLeftPushAsync(_listKey, _payload).ConfigureAwait(false));
                _consumer.Consume((await _serDb.ListLeftPopAsync(_listKey).ConfigureAwait(false)).HasValue);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    [Benchmark]
    [BenchmarkCategory("RedisEndToEndHeadToHead")]
    public async Task VapeCache()
    {
        switch (Operation)
        {
            case RedisEndToEndOperation.StringSet:
                _consumer.Consume(await _executor.SetAsync(_key, _payload, ttl: null, CancellationToken.None).ConfigureAwait(false));
                break;
            case RedisEndToEndOperation.StringGet:
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
            case RedisEndToEndOperation.HashSet:
                _consumer.Consume(await _executor.HSetAsync(_hashKey, Field, _payload, CancellationToken.None).ConfigureAwait(false));
                break;
            case RedisEndToEndOperation.HashGet:
                if (ReadPath == VapeReadPath.Lease)
                {
                    using var lease = await _executor.HGetLeaseAsync(_hashKey, Field, CancellationToken.None).ConfigureAwait(false);
                    _consumer.Consume(lease.Length);
                }
                else
                {
                    var bytes = await _executor.HGetAsync(_hashKey, Field, CancellationToken.None).ConfigureAwait(false);
                    _consumer.Consume(bytes?.Length ?? -1);
                }
                break;
            case RedisEndToEndOperation.ListPush:
                _consumer.Consume(await _executor.LPushAsync(_listKey, _payload, CancellationToken.None).ConfigureAwait(false));
                break;
            case RedisEndToEndOperation.ListPop:
                await _executor.DeleteAsync(_listKey, CancellationToken.None).ConfigureAwait(false);
                _consumer.Consume(await _executor.LPushAsync(_listKey, _payload, CancellationToken.None).ConfigureAwait(false));
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
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    public enum RedisEndToEndOperation
    {
        StringSet,
        StringGet,
        HashSet,
        HashGet,
        ListPush,
        ListPop
    }

    public enum VapeReadPath
    {
        Lease,
        Materialized
    }
}
