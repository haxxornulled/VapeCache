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
public class RedisDatatypeParityHeadToHeadBenchmarks
{
    private static readonly RedisDatatypeParityOperation[] FullOperations =
    [
        RedisDatatypeParityOperation.StringRoundTrip,
        RedisDatatypeParityOperation.HashRoundTrip,
        RedisDatatypeParityOperation.ListRoundTrip,
        RedisDatatypeParityOperation.SetRoundTrip,
        RedisDatatypeParityOperation.SortedSetRoundTrip
    ];

    private static readonly RedisDatatypeParityOperation[] QuickOperations =
    [
        RedisDatatypeParityOperation.StringRoundTrip,
        RedisDatatypeParityOperation.ListRoundTrip,
        RedisDatatypeParityOperation.SortedSetRoundTrip
    ];

    private static readonly int[] FullPayloadSizes = [256, 1024, 4096, 16384];
    private static readonly int[] QuickPayloadSizes = [256, 4096];

    private const string Field = "field";
    private const double Score = 42.5;

    [ParamsSource(nameof(Operations))]
    public RedisDatatypeParityOperation Operation { get; set; }

    [ParamsSource(nameof(PayloadSizes))]
    public int PayloadBytes { get; set; }

    [Params(false, true)]
    public bool EnableInstrumentation { get; set; }

    public IEnumerable<RedisDatatypeParityOperation> Operations =>
        BenchmarkRedisConfig.ResolveEnumParams("VAPECACHE_BENCH_DATATYPE_OPERATIONS", FullOperations, QuickOperations);

    public IEnumerable<int> PayloadSizes =>
        BenchmarkRedisConfig.ResolveIntParams("VAPECACHE_BENCH_DATATYPE_PAYLOADS", FullPayloadSizes, QuickPayloadSizes);

    private readonly Consumer _consumer = new();
    private byte[] _payload = Array.Empty<byte>();

    private ConnectionMultiplexer? _serMux;
    private IDatabase? _serDb;
    private RedisCommandExecutor? _executor;

    private string _stringKey = string.Empty;
    private string _hashKey = string.Empty;
    private string _listKey = string.Empty;
    private string _setKey = string.Empty;
    private string _sortedSetKey = string.Empty;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var options = BenchmarkRedisConfig.Load();
        var prefix = $"bench:datatype:{Guid.NewGuid():N}";

        _stringKey = $"{prefix}:str";
        _hashKey = $"{prefix}:hash";
        _listKey = $"{prefix}:list";
        _setKey = $"{prefix}:set";
        _sortedSetKey = $"{prefix}:zset";

        _payload = GC.AllocateUninitializedArray<byte>(PayloadBytes);
        BenchmarkRedisConfig.FillPayload(_payload, seed: 3000 + PayloadBytes);

        _serMux = await BenchmarkRedisConfig.ConnectStackExchangeAsync(options).ConfigureAwait(false);
        _serDb = _serMux.GetDatabase(options.Database);
        _executor = BenchmarkRedisConfig.CreateVapeCacheExecutor(
            options,
            connections: 1,
            maxInFlight: 4096,
            enableInstrumentation: EnableInstrumentation,
            enableCoalescedWrites: true,
            useDedicatedLaneWorkers: true,
            enableSocketRespReader: true);

        await DeleteAllAsync().ConfigureAwait(false);
        await SeedSharedStateAsync().ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        try
        {
            await DeleteAllAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        if (_executor is not null)
            await _executor.DisposeAsync().ConfigureAwait(false);

        try
        {
            _serMux?.Dispose();
        }
        catch
        {
        }
    }

    [BenchmarkCategory("RedisDatatypeParityHeadToHead")]
    [Benchmark(Baseline = true)]
    public async Task StackExchange()
    {
        switch (Operation)
        {
            case RedisDatatypeParityOperation.StringRoundTrip:
            {
                await _serDb!.StringSetAsync(_stringKey, _payload).ConfigureAwait(false);
                var value = await _serDb.StringGetAsync(_stringKey).ConfigureAwait(false);
                _consumer.Consume(value.HasValue ? ((byte[])value!).Length : -1);
                break;
            }

            case RedisDatatypeParityOperation.HashRoundTrip:
            {
                await _serDb!.HashSetAsync(_hashKey, Field, _payload).ConfigureAwait(false);
                var value = await _serDb.HashGetAsync(_hashKey, Field).ConfigureAwait(false);
                _consumer.Consume(value.HasValue ? ((byte[])value!).Length : -1);
                break;
            }

            case RedisDatatypeParityOperation.ListRoundTrip:
            {
                await _serDb!.KeyDeleteAsync(_listKey).ConfigureAwait(false);
                _consumer.Consume(await _serDb.ListLeftPushAsync(_listKey, _payload).ConfigureAwait(false));
                var value = await _serDb.ListLeftPopAsync(_listKey).ConfigureAwait(false);
                _consumer.Consume(value.HasValue ? ((byte[])value!).Length : -1);
                break;
            }

            case RedisDatatypeParityOperation.SetRoundTrip:
            {
                await _serDb!.KeyDeleteAsync(_setKey).ConfigureAwait(false);
                _consumer.Consume(await _serDb.SetAddAsync(_setKey, _payload).ConfigureAwait(false));
                _consumer.Consume(await _serDb.SetContainsAsync(_setKey, _payload).ConfigureAwait(false));
                _consumer.Consume(await _serDb.SetLengthAsync(_setKey).ConfigureAwait(false));
                break;
            }

            case RedisDatatypeParityOperation.SortedSetRoundTrip:
            {
                await _serDb!.KeyDeleteAsync(_sortedSetKey).ConfigureAwait(false);
                _consumer.Consume(await _serDb.SortedSetAddAsync(_sortedSetKey, _payload, Score).ConfigureAwait(false));
                _consumer.Consume(await _serDb.SortedSetScoreAsync(_sortedSetKey, _payload).ConfigureAwait(false));
                var values = await _serDb.SortedSetRangeByScoreWithScoresAsync(
                    _sortedSetKey,
                    start: Score,
                    stop: Score,
                    skip: 0,
                    take: 1,
                    order: Order.Ascending).ConfigureAwait(false);
                _consumer.Consume(values.Length);
                _consumer.Consume(await _serDb.SortedSetRemoveAsync(_sortedSetKey, _payload).ConfigureAwait(false));
                break;
            }

            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    [Benchmark]
    [BenchmarkCategory("RedisDatatypeParityHeadToHead")]
    public async Task VapeCache()
    {
        switch (Operation)
        {
            case RedisDatatypeParityOperation.StringRoundTrip:
            {
                _consumer.Consume(await _executor!.SetAsync(_stringKey, _payload, ttl: null, CancellationToken.None).ConfigureAwait(false));
                var value = await _executor.GetAsync(_stringKey, CancellationToken.None).ConfigureAwait(false);
                _consumer.Consume(value?.Length ?? -1);
                break;
            }

            case RedisDatatypeParityOperation.HashRoundTrip:
            {
                _consumer.Consume(await _executor!.HSetAsync(_hashKey, Field, _payload, CancellationToken.None).ConfigureAwait(false));
                var value = await _executor.HGetAsync(_hashKey, Field, CancellationToken.None).ConfigureAwait(false);
                _consumer.Consume(value?.Length ?? -1);
                break;
            }

            case RedisDatatypeParityOperation.ListRoundTrip:
            {
                await _executor!.DeleteAsync(_listKey, CancellationToken.None).ConfigureAwait(false);
                _consumer.Consume(await _executor.LPushAsync(_listKey, _payload, CancellationToken.None).ConfigureAwait(false));
                var value = await _executor.LPopAsync(_listKey, CancellationToken.None).ConfigureAwait(false);
                _consumer.Consume(value?.Length ?? -1);
                break;
            }

            case RedisDatatypeParityOperation.SetRoundTrip:
            {
                await _executor!.DeleteAsync(_setKey, CancellationToken.None).ConfigureAwait(false);
                _consumer.Consume(await _executor.SAddAsync(_setKey, _payload, CancellationToken.None).ConfigureAwait(false));
                _consumer.Consume(await _executor.SIsMemberAsync(_setKey, _payload, CancellationToken.None).ConfigureAwait(false));
                _consumer.Consume(await _executor.SCardAsync(_setKey, CancellationToken.None).ConfigureAwait(false));
                break;
            }

            case RedisDatatypeParityOperation.SortedSetRoundTrip:
            {
                await _executor!.DeleteAsync(_sortedSetKey, CancellationToken.None).ConfigureAwait(false);
                _consumer.Consume(await _executor.ZAddAsync(_sortedSetKey, Score, _payload, CancellationToken.None).ConfigureAwait(false));
                _consumer.Consume(await _executor.ZScoreAsync(_sortedSetKey, _payload, CancellationToken.None).ConfigureAwait(false));
                var values = await _executor.ZRangeByScoreWithScoresAsync(
                    _sortedSetKey,
                    min: Score,
                    max: Score,
                    descending: false,
                    offset: 0,
                    count: 1,
                    CancellationToken.None).ConfigureAwait(false);
                _consumer.Consume(values.Length);
                _consumer.Consume(await _executor.ZRemAsync(_sortedSetKey, _payload, CancellationToken.None).ConfigureAwait(false));
                break;
            }

            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private async Task DeleteAllAsync()
    {
        if (_serDb is not null)
        {
            await _serDb.KeyDeleteAsync(new RedisKey[]
            {
                _stringKey,
                _hashKey,
                _listKey,
                _setKey,
                _sortedSetKey
            }).ConfigureAwait(false);
        }

        if (_executor is null)
            return;

        await _executor.DeleteAsync(_stringKey, CancellationToken.None).ConfigureAwait(false);
        await _executor.DeleteAsync(_hashKey, CancellationToken.None).ConfigureAwait(false);
        await _executor.DeleteAsync(_listKey, CancellationToken.None).ConfigureAwait(false);
        await _executor.DeleteAsync(_setKey, CancellationToken.None).ConfigureAwait(false);
        await _executor.DeleteAsync(_sortedSetKey, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SeedSharedStateAsync()
    {
        await _serDb!.StringSetAsync(_stringKey, _payload).ConfigureAwait(false);
        await _serDb.HashSetAsync(_hashKey, Field, _payload).ConfigureAwait(false);

        await _executor!.SetAsync(_stringKey, _payload, ttl: null, CancellationToken.None).ConfigureAwait(false);
        await _executor.HSetAsync(_hashKey, Field, _payload, CancellationToken.None).ConfigureAwait(false);
    }

    public enum RedisDatatypeParityOperation
    {
        StringRoundTrip,
        HashRoundTrip,
        ListRoundTrip,
        SetRoundTrip,
        SortedSetRoundTrip
    }
}
