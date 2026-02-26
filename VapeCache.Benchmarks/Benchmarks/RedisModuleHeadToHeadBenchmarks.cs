using System.Buffers.Binary;
using System.Text;
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
[BenchmarkCategory("RedisModules")]
public class RedisModuleHeadToHeadBenchmarks
{
    private static readonly RedisModuleOperation[] FullOperations =
    [
        RedisModuleOperation.JsonSet,
        RedisModuleOperation.JsonGet,
        RedisModuleOperation.FtSearch,
        RedisModuleOperation.BfAdd,
        RedisModuleOperation.BfExists,
        RedisModuleOperation.TsAdd,
        RedisModuleOperation.TsRange
    ];

    private static readonly RedisModuleOperation[] QuickOperations =
    [
        RedisModuleOperation.JsonSet,
        RedisModuleOperation.JsonGet,
        RedisModuleOperation.BfExists
    ];

    private static readonly int[] FullJsonPayloadChars = [128, 1024];
    private static readonly int[] QuickJsonPayloadChars = [128];

    [ParamsSource(nameof(Operations))]
    public RedisModuleOperation Operation { get; set; }

    [ParamsSource(nameof(JsonPayloadSizes))]
    public int JsonPayloadChars { get; set; }

    public IEnumerable<RedisModuleOperation> Operations =>
        BenchmarkRedisConfig.ResolveEnumParams("VAPECACHE_BENCH_MODULE_OPERATIONS", FullOperations, QuickOperations);

    public IEnumerable<int> JsonPayloadSizes =>
        BenchmarkRedisConfig.ResolveIntParams("VAPECACHE_BENCH_MODULE_JSON_CHARS", FullJsonPayloadChars, QuickJsonPayloadChars);

    private readonly Consumer _consumer = new();

    private ConnectionMultiplexer? _ser;
    private IDatabase? _db;
    private RedisCommandExecutor? _executor;

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
        _ser = await BenchmarkRedisConfig.ConnectStackExchangeAsync(options).ConfigureAwait(false);
        _db = _ser.GetDatabase(options.Database);
        _executor = BenchmarkRedisConfig.CreateVapeCacheExecutor(options, enableInstrumentation: false);

        var payload = new string('a', JsonPayloadChars);
        _jsonPayload = Encoding.UTF8.GetBytes($"{{\"name\":\"bench\",\"payload\":\"{payload}\"}}");

        var prefix = "bench:modules:h2h:" + Guid.NewGuid().ToString("N");
        _jsonKey = prefix + ":json";
        _searchIndex = prefix + ":idx";
        _searchPrefix = prefix + ":doc:";
        _docKey = _searchPrefix + "1";
        _bloomKey = prefix + ":bf";
        _tsKey = prefix + ":ts";

        var moduleList = await _db.ExecuteAsync("MODULE", "LIST").ConfigureAwait(false);
        var modules = ParseModuleNames(moduleList);
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

        await _db.ExecuteAsync("JSON.SET", _jsonKey, ".", _jsonPayload).ConfigureAwait(false);
        await _db.ExecuteAsync(
                "FT.CREATE",
                _searchIndex,
                "ON", "HASH",
                "PREFIX", 1, _searchPrefix,
                "SCHEMA", "title", "TEXT", "body", "TEXT")
            .ConfigureAwait(false);
        await _db.HashSetAsync(
                _docKey,
                new[]
                {
                    new HashEntry("title", "bench"),
                    new HashEntry("body", "bench body text")
                })
            .ConfigureAwait(false);
        await _db.ExecuteAsync("FT.SEARCH", _searchIndex, "*", "LIMIT", 0, 1).ConfigureAwait(false);

        Random.Shared.NextBytes(_bloomItem);
        Array.Copy(_bloomItem, _bloomExistsItem, _bloomItem.Length);
        await _db.ExecuteAsync("BF.ADD", _bloomKey, _bloomExistsItem).ConfigureAwait(false);

        _tsCursor = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _db.ExecuteAsync("TS.CREATE", _tsKey).ConfigureAwait(false);
        await _db.ExecuteAsync("TS.ADD", _tsKey, _tsCursor, 1).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_db is not null)
        {
            try { await _db.ExecuteAsync("FT.DROPINDEX", _searchIndex, "DD").ConfigureAwait(false); } catch { }
            try { await _db.KeyDeleteAsync(_jsonKey).ConfigureAwait(false); } catch { }
            try { await _db.KeyDeleteAsync(_bloomKey).ConfigureAwait(false); } catch { }
            try { await _db.KeyDeleteAsync(_tsKey).ConfigureAwait(false); } catch { }
        }

        if (_executor is not null)
            await _executor.DisposeAsync().ConfigureAwait(false);
        _ser?.Dispose();
    }

    [BenchmarkCategory("RedisModuleHeadToHead")]
    [Benchmark(Baseline = true)]
    public async Task StackExchange()
    {
        switch (Operation)
        {
            case RedisModuleOperation.JsonSet:
                _consumer.Consume(await _db!.ExecuteAsync("JSON.SET", _jsonKey, ".", _jsonPayload).ConfigureAwait(false));
                break;
            case RedisModuleOperation.JsonGet:
                _consumer.Consume(await _db!.ExecuteAsync("JSON.GET", _jsonKey, ".").ConfigureAwait(false));
                break;
            case RedisModuleOperation.FtSearch:
                _consumer.Consume(await _db!.ExecuteAsync("FT.SEARCH", _searchIndex, "*", "LIMIT", 0, 10).ConfigureAwait(false));
                break;
            case RedisModuleOperation.BfAdd:
                NextBloomItem();
                _consumer.Consume(await _db!.ExecuteAsync("BF.ADD", _bloomKey, _bloomItem).ConfigureAwait(false));
                break;
            case RedisModuleOperation.BfExists:
                _consumer.Consume(await _db!.ExecuteAsync("BF.EXISTS", _bloomKey, _bloomExistsItem).ConfigureAwait(false));
                break;
            case RedisModuleOperation.TsAdd:
                _consumer.Consume(await _db!.ExecuteAsync("TS.ADD", _tsKey, NextTimestamp(), 1).ConfigureAwait(false));
                break;
            case RedisModuleOperation.TsRange:
                var end = Volatile.Read(ref _tsCursor);
                _consumer.Consume(await _db!.ExecuteAsync("TS.RANGE", _tsKey, end - 1000, end).ConfigureAwait(false));
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    [Benchmark]
    [BenchmarkCategory("RedisModuleHeadToHead")]
    public async Task VapeCache()
    {
        switch (Operation)
        {
            case RedisModuleOperation.JsonSet:
                _consumer.Consume(await _executor!.JsonSetAsync(_jsonKey, ".", _jsonPayload, CancellationToken.None).ConfigureAwait(false));
                break;
            case RedisModuleOperation.JsonGet:
                _consumer.Consume((await _executor!.JsonGetAsync(_jsonKey, ".", CancellationToken.None).ConfigureAwait(false))?.Length ?? 0);
                break;
            case RedisModuleOperation.FtSearch:
                _consumer.Consume((await _executor!.FtSearchAsync(_searchIndex, "*", 0, 10, CancellationToken.None).ConfigureAwait(false)).Length);
                break;
            case RedisModuleOperation.BfAdd:
                NextBloomItem();
                _consumer.Consume(await _executor!.BfAddAsync(_bloomKey, _bloomItem, CancellationToken.None).ConfigureAwait(false));
                break;
            case RedisModuleOperation.BfExists:
                _consumer.Consume(await _executor!.BfExistsAsync(_bloomKey, _bloomExistsItem, CancellationToken.None).ConfigureAwait(false));
                break;
            case RedisModuleOperation.TsAdd:
                _consumer.Consume(await _executor!.TsAddAsync(_tsKey, NextTimestamp(), 1, CancellationToken.None).ConfigureAwait(false));
                break;
            case RedisModuleOperation.TsRange:
                var end = Volatile.Read(ref _tsCursor);
                _consumer.Consume((await _executor!.TsRangeAsync(_tsKey, end - 1000, end, CancellationToken.None).ConfigureAwait(false)).Length);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void NextBloomItem()
    {
        var next = Interlocked.Increment(ref _bloomCounter);
        BinaryPrimitives.WriteInt64LittleEndian(_bloomItem.AsSpan(0, 8), next);
    }

    private long NextTimestamp() => Interlocked.Increment(ref _tsCursor);

    private static bool HasModule(string[] modules, params string[] names)
        => modules.Any(module => names.Any(name => string.Equals(module, name, StringComparison.OrdinalIgnoreCase)));

    private static string[] ParseModuleNames(RedisResult result)
    {
        if (result.IsNull)
            return Array.Empty<string>();

        RedisResult[]? modules;
        try
        {
            modules = (RedisResult[]?)result;
        }
        catch
        {
            return Array.Empty<string>();
        }

        if (modules is null)
            return Array.Empty<string>();

        var names = new List<string>(modules.Length);
        foreach (var module in modules)
        {
            RedisResult[]? fields;
            try
            {
                fields = (RedisResult[]?)module;
            }
            catch
            {
                continue;
            }

            if (fields is null)
                continue;

            for (var i = 0; i + 1 < fields.Length; i += 2)
            {
                var key = fields[i].ToString() ?? string.Empty;
                if (!string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = fields[i + 1].ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    names.Add(value);
                break;
            }
        }

        return names.ToArray();
    }

    public enum RedisModuleOperation
    {
        JsonSet,
        JsonGet,
        FtSearch,
        BfAdd,
        BfExists,
        TsAdd,
        TsRange
    }
}
