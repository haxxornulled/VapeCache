using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using StackExchange.Redis;
using VapeCache.Abstractions.Connections;
using VapeCache.Benchmarks;

namespace VapeCache.Benchmarks.Benchmarks;

[MemoryDiagnoser(displayGenColumns: true)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(warmupCount: 3, iterationCount: 12)]
public class RedisClientStackExchangeBenchmarks
{
    [Params(256, 2048)]
    public int PayloadBytes { get; set; }

    private readonly Consumer _consumer = new();
    private byte[] _payload = Array.Empty<byte>();

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

        var options = BenchmarkRedisConfig.Load();

        _key = "bench:ser:" + Guid.NewGuid().ToString("N");
        _hashKey = _key + ":h";
        _listKey = _key + ":l";

        _ser = await BenchmarkRedisConfig.ConnectStackExchangeAsync(options).ConfigureAwait(false);
        _db = _ser.GetDatabase(options.Database);

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

        _ser?.Dispose();
    }

    [Benchmark]
    [BenchmarkCategory("StringSetGet")]
    public async Task SER_StringSetGet()
    {
        var db = _db!;
        await db.StringSetAsync(_key, _payload).ConfigureAwait(false);
        _ = await db.StringGetAsync(_key).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("HashSetGet")]
    public async Task SER_HashSetGet()
    {
        var db = _db!;
        await db.HashSetAsync(_hashKey, _field, _payload).ConfigureAwait(false);
        _ = await db.HashGetAsync(_hashKey, _field).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("ListPushPop")]
    public async Task SER_ListPushPop()
    {
        var db = _db!;
        _ = await db.ListLeftPushAsync(_listKey, _payload).ConfigureAwait(false);
        _ = await db.ListLeftPopAsync(_listKey).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("Ping")]
    public async Task SER_Ping()
    {
        var db = _db!;
        var result = await db.ExecuteAsync("PING").ConfigureAwait(false);
        _consumer.Consume(result);
    }

    [Benchmark]
    [BenchmarkCategory("ModuleList")]
    public async Task SER_ModuleList()
    {
        var db = _db!;
        var result = await db.ExecuteAsync("MODULE", "LIST").ConfigureAwait(false);
        _consumer.Consume(result);
    }
}
