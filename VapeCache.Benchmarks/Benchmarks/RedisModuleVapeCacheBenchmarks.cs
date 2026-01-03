using System.Buffers.Binary;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Order;
using StackExchange.Redis;
using VapeCache.Abstractions.Connections;
using VapeCache.Benchmarks;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

[MemoryDiagnoser(displayGenColumns: true)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(warmupCount: 3, iterationCount: 12)]
[BenchmarkCategory("RedisModules")]
public class RedisModuleVapeCacheBenchmarks
{
    [Params(128, 1024)]
    public int JsonPayloadChars { get; set; }

    private readonly Consumer _consumer = new();

    private RedisCommandExecutor? _executor;
    private ConnectionMultiplexer? _cleanupMux;
    private IDatabase? _cleanupDb;

    private string _jsonKey = string.Empty;
    private string _searchIndex = string.Empty;
    private string _searchPrefix = string.Empty;
    private string _docKey = string.Empty;
    private string _bloomKey = string.Empty;
    private string _tsKey = string.Empty;

    private byte[] _jsonPayload = Array.Empty<byte>();
    private readonly byte[] _bloomItem = new byte[16];
    private readonly byte[] _bloomExistsItem = new byte[16];
    private long _bloomCounter;
    private long _tsCursor;

    [GlobalSetup]
    public async Task Setup()
    {
        var options = BenchmarkRedisConfig.Load();

        var payload = new string('a', JsonPayloadChars);
        _jsonPayload = Encoding.UTF8.GetBytes($"{{\"name\":\"bench\",\"payload\":\"{payload}\"}}");

        var prefix = "bench:modules:vape:" + Guid.NewGuid().ToString("N");
        _jsonKey = prefix + ":json";
        _searchIndex = prefix + ":idx";
        _searchPrefix = prefix + ":doc:";
        _docKey = _searchPrefix + "1";
        _bloomKey = prefix + ":bf";
        _tsKey = prefix + ":ts";

        _executor = BenchmarkRedisConfig.CreateVapeCacheExecutor(options);
        _cleanupMux = await BenchmarkRedisConfig.ConnectStackExchangeAsync(options).ConfigureAwait(false);
        _cleanupDb = _cleanupMux.GetDatabase(options.Database);

        var modules = await Step("MODULE LIST", () => _executor.ModuleListAsync(CancellationToken.None)).ConfigureAwait(false);
        var missing = new List<string>();
        if (!HasModule(modules, "ReJSON", "ReJSON-RL"))
            missing.Add("RedisJSON");
        if (!HasModule(modules, "search"))
            missing.Add("RediSearch");
        if (!HasModule(modules, "bf", "bloom"))
            missing.Add("RedisBloom");
        if (!HasModule(modules, "timeseries", "ts"))
            missing.Add("RedisTimeSeries");

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Redis module benchmarks require: " + string.Join(", ", missing) +
                ". Install the modules or run benchmarks without RedisModules categories.");
        }

        await Step("JSON.SET seed", () => _executor.JsonSetAsync(_jsonKey, ".", _jsonPayload, CancellationToken.None)).ConfigureAwait(false);

        var indexOk = await Step("FT.CREATE", () => _executor.FtCreateAsync(
                _searchIndex,
                _searchPrefix,
                new[] { "title", "body" },
                CancellationToken.None))
            .ConfigureAwait(false);
        _consumer.Consume(indexOk);

        await Step("HSET title", () => _executor.HSetAsync(_docKey, "title", "bench"u8.ToArray(), CancellationToken.None)).ConfigureAwait(false);
        await Step("HSET body", () => _executor.HSetAsync(_docKey, "body", "bench body text"u8.ToArray(), CancellationToken.None)).ConfigureAwait(false);

        await Step("FT.SEARCH warm", () => _executor.FtSearchAsync(_searchIndex, "*", 0, 1, CancellationToken.None)).ConfigureAwait(false);

        Random.Shared.NextBytes(_bloomItem);
        Array.Copy(_bloomItem, _bloomExistsItem, _bloomItem.Length);
        await Step("BF.ADD seed", () => _executor.BfAddAsync(_bloomKey, _bloomExistsItem, CancellationToken.None)).ConfigureAwait(false);

        _tsCursor = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await Step("TS.CREATE", () => _executor.TsCreateAsync(_tsKey, CancellationToken.None)).ConfigureAwait(false);
        await Step("TS.ADD seed", () => _executor.TsAddAsync(_tsKey, _tsCursor, 1, CancellationToken.None)).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_cleanupDb is not null)
        {
            try { await _cleanupDb.ExecuteAsync("FT.DROPINDEX", _searchIndex, "DD").ConfigureAwait(false); } catch { }
            try { await _cleanupDb.KeyDeleteAsync(_jsonKey).ConfigureAwait(false); } catch { }
            try { await _cleanupDb.KeyDeleteAsync(_bloomKey).ConfigureAwait(false); } catch { }
            try { await _cleanupDb.KeyDeleteAsync(_tsKey).ConfigureAwait(false); } catch { }
        }

        if (_executor is not null)
            await _executor.DisposeAsync().ConfigureAwait(false);
        _cleanupMux?.Dispose();
    }

    [Benchmark]
    [BenchmarkCategory("JSON.SET")]
    public async Task Ours_JsonSet()
    {
        var ok = await _executor!.JsonSetAsync(_jsonKey, ".", _jsonPayload, CancellationToken.None).ConfigureAwait(false);
        _consumer.Consume(ok);
    }

    [Benchmark]
    [BenchmarkCategory("JSON.GET")]
    public async Task Ours_JsonGet()
    {
        var result = await _executor!.JsonGetAsync(_jsonKey, ".", CancellationToken.None).ConfigureAwait(false);
        _consumer.Consume(result?.Length ?? 0);
    }

    [Benchmark]
    [BenchmarkCategory("FT.SEARCH")]
    public async Task Ours_FtSearch()
    {
        var result = await _executor!.FtSearchAsync(_searchIndex, "*", 0, 10, CancellationToken.None).ConfigureAwait(false);
        _consumer.Consume(result.Length);
    }

    [Benchmark]
    [BenchmarkCategory("BF.ADD")]
    public async Task Ours_BfAdd()
    {
        NextBloomItem();
        var result = await _executor!.BfAddAsync(_bloomKey, _bloomItem, CancellationToken.None).ConfigureAwait(false);
        _consumer.Consume(result);
    }

    [Benchmark]
    [BenchmarkCategory("BF.EXISTS")]
    public async Task Ours_BfExists()
    {
        var result = await _executor!.BfExistsAsync(_bloomKey, _bloomExistsItem, CancellationToken.None).ConfigureAwait(false);
        _consumer.Consume(result);
    }

    [Benchmark]
    [BenchmarkCategory("TS.ADD")]
    public async Task Ours_TsAdd()
    {
        var ts = NextTimestamp();
        var result = await _executor!.TsAddAsync(_tsKey, ts, 1, CancellationToken.None).ConfigureAwait(false);
        _consumer.Consume(result);
    }

    [Benchmark]
    [BenchmarkCategory("TS.RANGE")]
    public async Task Ours_TsRange()
    {
        var end = Volatile.Read(ref _tsCursor);
        var result = await _executor!.TsRangeAsync(_tsKey, end - 1000, end, CancellationToken.None).ConfigureAwait(false);
        _consumer.Consume(result.Length);
    }

    private void NextBloomItem()
    {
        var next = Interlocked.Increment(ref _bloomCounter);
        BinaryPrimitives.WriteInt64LittleEndian(_bloomItem.AsSpan(0, 8), next);
    }

    private long NextTimestamp()
        => Interlocked.Increment(ref _tsCursor);

    private static bool HasModule(string[] modules, params string[] names)
        => modules.Any(module => names.Any(name => string.Equals(module, name, StringComparison.OrdinalIgnoreCase)));

    private static async Task<T> Step<T>(string name, Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{name} failed.", ex);
        }
    }

    private static async Task Step(string name, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{name} failed.", ex);
        }
    }

    private static async Task<T> Step<T>(string name, Func<ValueTask<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{name} failed.", ex);
        }
    }

    private static async Task Step(string name, Func<ValueTask> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{name} failed.", ex);
        }
    }
}
