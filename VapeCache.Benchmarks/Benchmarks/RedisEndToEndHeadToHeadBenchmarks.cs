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

    [Params(
        RedisEndToEndOperation.StringSet,
        RedisEndToEndOperation.StringGet,
        RedisEndToEndOperation.HashSet,
        RedisEndToEndOperation.HashGet,
        RedisEndToEndOperation.ListPush,
        RedisEndToEndOperation.ListPop)]
    public RedisEndToEndOperation Operation { get; set; }

    [Params(32, 256, 4096)]
    public int PayloadBytes { get; set; }

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
        new Random(42).NextBytes(_payload);

        _serMux = await BenchmarkRedisConfig.ConnectStackExchangeAsync(options).ConfigureAwait(false);
        _serDb = _serMux.GetDatabase(options.Database);
        _executor = BenchmarkRedisConfig.CreateVapeCacheExecutor(
            options,
            connections: 1,
            maxInFlight: 4096,
            enableInstrumentation: false,
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
                using (var lease = await _executor.GetLeaseAsync(_key, CancellationToken.None).ConfigureAwait(false))
                    _consumer.Consume(lease.Length);
                break;
            case RedisEndToEndOperation.HashSet:
                _consumer.Consume(await _executor.HSetAsync(_hashKey, Field, _payload, CancellationToken.None).ConfigureAwait(false));
                break;
            case RedisEndToEndOperation.HashGet:
                using (var lease = await _executor.HGetLeaseAsync(_hashKey, Field, CancellationToken.None).ConfigureAwait(false))
                    _consumer.Consume(lease.Length);
                break;
            case RedisEndToEndOperation.ListPush:
                _consumer.Consume(await _executor.LPushAsync(_listKey, _payload, CancellationToken.None).ConfigureAwait(false));
                break;
            case RedisEndToEndOperation.ListPop:
                await _executor.DeleteAsync(_listKey, CancellationToken.None).ConfigureAwait(false);
                _consumer.Consume(await _executor.LPushAsync(_listKey, _payload, CancellationToken.None).ConfigureAwait(false));
                using (var lease = await _executor.LPopLeaseAsync(_listKey, CancellationToken.None).ConfigureAwait(false))
                    _consumer.Consume(lease.Length);
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
}
