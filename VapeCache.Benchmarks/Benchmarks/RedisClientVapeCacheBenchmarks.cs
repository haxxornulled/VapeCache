using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using VapeCache.Abstractions.Connections;
using VapeCache.Benchmarks;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

[MemoryDiagnoser(displayGenColumns: true)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(warmupCount: 3, iterationCount: 12)]
public class RedisClientVapeCacheBenchmarks
{
    [Params(256, 2048)]
    public int PayloadBytes { get; set; }

    private readonly Consumer _consumer = new();
    private byte[] _payload = Array.Empty<byte>();

    private RedisCommandExecutor? _executor;

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

        _key = "bench:vape:" + Guid.NewGuid().ToString("N");
        _hashKey = _key + ":h";
        _listKey = _key + ":l";

        _executor = BenchmarkRedisConfig.CreateVapeCacheExecutor(options);

        await _executor.SetAsync(_key, _payload, ttl: null, CancellationToken.None).ConfigureAwait(false);
        await _executor.HSetAsync(_hashKey, _field, _payload, CancellationToken.None).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_executor is not null)
        {
            await _executor.DeleteAsync(_key, CancellationToken.None).ConfigureAwait(false);
            await _executor.DeleteAsync(_hashKey, CancellationToken.None).ConfigureAwait(false);
            await _executor.DeleteAsync(_listKey, CancellationToken.None).ConfigureAwait(false);
            await _executor.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Benchmark]
    [BenchmarkCategory("StringSetGet")]
    public async Task Ours_StringSetGet()
    {
        var ours = _executor!;
        _ = await ours.SetAsync(_key, _payload, ttl: null, CancellationToken.None).ConfigureAwait(false);
        using var lease = await ours.GetLeaseAsync(_key, CancellationToken.None).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("HashSetGet")]
    public async Task Ours_HashSetGet()
    {
        var ours = _executor!;
        _ = await ours.HSetAsync(_hashKey, _field, _payload, CancellationToken.None).ConfigureAwait(false);
        using var lease = await ours.HGetLeaseAsync(_hashKey, _field, CancellationToken.None).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("ListPushPop")]
    public async Task Ours_ListPushPop()
    {
        var ours = _executor!;
        _ = await ours.LPushAsync(_listKey, _payload, CancellationToken.None).ConfigureAwait(false);
        using var lease = await ours.LPopLeaseAsync(_listKey, CancellationToken.None).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("Ping")]
    public async Task Ours_Ping()
    {
        var ours = _executor!;
        var result = await ours.PingAsync(CancellationToken.None).ConfigureAwait(false);
        _consumer.Consume(result);
    }

    [Benchmark]
    [BenchmarkCategory("ModuleList")]
    public async Task Ours_ModuleList()
    {
        var ours = _executor!;
        var result = await ours.ModuleListAsync(CancellationToken.None).ConfigureAwait(false);
        _consumer.Consume(result.Length);
    }
}
