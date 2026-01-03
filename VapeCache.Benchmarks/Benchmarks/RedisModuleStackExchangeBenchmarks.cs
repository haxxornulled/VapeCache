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

namespace VapeCache.Benchmarks.Benchmarks;

[MemoryDiagnoser(displayGenColumns: true)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(warmupCount: 3, iterationCount: 12)]
[BenchmarkCategory("RedisModules")]
public class RedisModuleStackExchangeBenchmarks
{
    [Params(128, 1024)]
    public int JsonPayloadChars { get; set; }

    private readonly Consumer _consumer = new();

    private ConnectionMultiplexer? _ser;
    private IDatabase? _db;

    private string _jsonKey = string.Empty;
    private string _searchIndex = string.Empty;
    private string _searchPrefix = string.Empty;
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

        var prefix = "bench:modules:ser:" + Guid.NewGuid().ToString("N");
        _jsonKey = prefix + ":json";
        _searchIndex = prefix + ":idx";
        _searchPrefix = prefix + ":doc:";
        _bloomKey = prefix + ":bf";
        _tsKey = prefix + ":ts";

        _ser = await BenchmarkRedisConfig.ConnectStackExchangeAsync(options).ConfigureAwait(false);
        _db = _ser.GetDatabase(options.Database);

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

        var docKey = _searchPrefix + "1";
        await _db.HashSetAsync(
                docKey,
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

        _ser?.Dispose();
    }

    [Benchmark]
    [BenchmarkCategory("JSON.SET")]
    public async Task SER_JsonSet()
    {
        var result = await _db!.ExecuteAsync("JSON.SET", _jsonKey, ".", _jsonPayload).ConfigureAwait(false);
        _consumer.Consume(result);
    }

    [Benchmark]
    [BenchmarkCategory("JSON.GET")]
    public async Task SER_JsonGet()
    {
        var result = await _db!.ExecuteAsync("JSON.GET", _jsonKey, ".").ConfigureAwait(false);
        _consumer.Consume(result);
    }

    [Benchmark]
    [BenchmarkCategory("FT.SEARCH")]
    public async Task SER_FtSearch()
    {
        var result = await _db!.ExecuteAsync("FT.SEARCH", _searchIndex, "*", "LIMIT", 0, 10).ConfigureAwait(false);
        _consumer.Consume(result);
    }

    [Benchmark]
    [BenchmarkCategory("BF.ADD")]
    public async Task SER_BfAdd()
    {
        NextBloomItem();
        var result = await _db!.ExecuteAsync("BF.ADD", _bloomKey, _bloomItem).ConfigureAwait(false);
        _consumer.Consume(result);
    }

    [Benchmark]
    [BenchmarkCategory("BF.EXISTS")]
    public async Task SER_BfExists()
    {
        var result = await _db!.ExecuteAsync("BF.EXISTS", _bloomKey, _bloomExistsItem).ConfigureAwait(false);
        _consumer.Consume(result);
    }

    [Benchmark]
    [BenchmarkCategory("TS.ADD")]
    public async Task SER_TsAdd()
    {
        var ts = NextTimestamp();
        var result = await _db!.ExecuteAsync("TS.ADD", _tsKey, ts, 1).ConfigureAwait(false);
        _consumer.Consume(result);
    }

    [Benchmark]
    [BenchmarkCategory("TS.RANGE")]
    public async Task SER_TsRange()
    {
        var end = Volatile.Read(ref _tsCursor);
        var result = await _db!.ExecuteAsync("TS.RANGE", _tsKey, end - 1000, end).ConfigureAwait(false);
        _consumer.Consume(result);
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
}
