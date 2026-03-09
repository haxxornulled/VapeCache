using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using VapeCache.Abstractions.Connections;
using VapeCache.Benchmarks;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

[Config(typeof(EnterpriseBenchmarkConfig))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class RedisEndToEndVapeCacheBenchmarks
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

    private RedisCommandExecutor _executor = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var options = BenchmarkRedisConfig.Load();

        var prefix = $"bench:vape:{Guid.NewGuid():N}";
        _key = $"{prefix}:str";
        _hashKey = $"{prefix}:hash";
        _listKey = $"{prefix}:list";

        _payload = GC.AllocateUninitializedArray<byte>(PayloadBytes);
        new Random(42).NextBytes(_payload);

        _executor = BenchmarkRedisConfig.CreateVapeCacheExecutor(
            options,
            connections: Connections,
            maxInFlight: 4096,
            enableInstrumentation: false,
            enableCoalescedWrites: true);

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
    }

    [Benchmark]
    [BenchmarkCategory("STRING_SET")]
    public async Task Ours_StringSet()
    {
        var ok = await _executor.SetAsync(_key, _payload, ttl: null, CancellationToken.None).ConfigureAwait(false);
        _consumer.Consume(ok);
    }

    [Benchmark]
    [BenchmarkCategory("STRING_GET")]
    public async Task Ours_StringGetLease()
    {
        using var lease = await _executor.GetLeaseAsync(_key, CancellationToken.None).ConfigureAwait(false);
        _consumer.Consume(lease.Length);
    }

    [Benchmark]
    [BenchmarkCategory("HASH_HSET")]
    public async Task Ours_HashSet()
    {
        var n = await _executor.HSetAsync(_hashKey, Field, _payload, CancellationToken.None).ConfigureAwait(false);
        _consumer.Consume(n);
    }

    [Benchmark]
    [BenchmarkCategory("HASH_HGET")]
    public async Task Ours_HashGetLease()
    {
        using var lease = await _executor.HGetLeaseAsync(_hashKey, Field, CancellationToken.None).ConfigureAwait(false);
        _consumer.Consume(lease.Length);
    }

    [Benchmark]
    [BenchmarkCategory("LIST_LPUSH")]
    public async Task Ours_ListPush()
    {
        var n = await _executor.LPushAsync(_listKey, _payload, CancellationToken.None).ConfigureAwait(false);
        _consumer.Consume(n);
    }

    [IterationSetup(Target = nameof(Ours_ListPopLease))]
    public void Ours_ListPop_SetupAsync()
    {
        _executor.DeleteAsync(_listKey, CancellationToken.None).GetAwaiter().GetResult();
        _executor.LPushAsync(_listKey, _payload, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Benchmark]
    [BenchmarkCategory("LIST_LPOP")]
    public async Task Ours_ListPopLease()
    {
        using var lease = await _executor.LPopLeaseAsync(_listKey, CancellationToken.None).ConfigureAwait(false);
        _consumer.Consume(lease.Length);
    }
}
