using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using StackExchange.Redis;
using VapeCache.Abstractions.Connections;
using VapeCache.Benchmarks;

namespace VapeCache.Benchmarks.Benchmarks;

[Config(typeof(EnterpriseBenchmarkConfig))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class RedisEndToEndStackExchangeBenchmarks
{
    private const string Field = "f";

    [Params(32, 256, 4096)]
    public int PayloadBytes { get; set; }

    private readonly Consumer _consumer = new();

    private string _key = null!;
    private string _hashKey = null!;
    private string _listKey = null!;

    private byte[] _payload = null!;

    private ConnectionMultiplexer _serMux = null!;
    private IDatabase _serDb = null!;

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

        await _serDb.KeyDeleteAsync(new RedisKey[] { _key, _hashKey, _listKey }).ConfigureAwait(false);
        await _serDb.StringSetAsync(_key, _payload).ConfigureAwait(false);
        await _serDb.HashSetAsync(_hashKey, Field, _payload).ConfigureAwait(false);
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
    }

    [Benchmark]
    [BenchmarkCategory("STRING_SET")]
    public async Task Ser_StringSet()
    {
        await _serDb.StringSetAsync(_key, _payload).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("STRING_GET")]
    public async Task Ser_StringGet()
    {
        var v = await _serDb.StringGetAsync(_key).ConfigureAwait(false);
        _consumer.Consume(v.HasValue);
    }

    [Benchmark]
    [BenchmarkCategory("HASH_HSET")]
    public async Task Ser_HashSet()
    {
        await _serDb.HashSetAsync(_hashKey, Field, _payload).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("HASH_HGET")]
    public async Task Ser_HashGet()
    {
        var v = await _serDb.HashGetAsync(_hashKey, Field).ConfigureAwait(false);
        _consumer.Consume(v.HasValue);
    }

    [Benchmark]
    [BenchmarkCategory("LIST_LPUSH")]
    public async Task Ser_ListPush()
    {
        await _serDb.ListLeftPushAsync(_listKey, _payload).ConfigureAwait(false);
    }

    [IterationSetup(Target = nameof(Ser_ListPop))]
    public void Ser_ListPop_SetupAsync()
    {
        _serDb.KeyDeleteAsync(_listKey).GetAwaiter().GetResult();
        _serDb.ListLeftPushAsync(_listKey, _payload).GetAwaiter().GetResult();
    }

    [Benchmark]
    [BenchmarkCategory("LIST_LPOP")]
    public async Task Ser_ListPop()
    {
        var v = await _serDb.ListLeftPopAsync(_listKey).ConfigureAwait(false);
        _consumer.Consume(v.HasValue);
    }
}
