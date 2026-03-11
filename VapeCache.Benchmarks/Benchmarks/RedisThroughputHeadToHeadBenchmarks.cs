using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using StackExchange.Redis;
using VapeCache.Benchmarks;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

[Config(typeof(EnterpriseBenchmarkConfig))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class RedisThroughputHeadToHeadBenchmarks
{
    private const string HashField = "f1";
    private const int DefaultTotalOperationsPerInvocation = 8192;

    private static readonly RedisThroughputOperation[] FullOperations =
    [
        RedisThroughputOperation.StringSetGet50_50,
        RedisThroughputOperation.HashSetGet50_50,
        RedisThroughputOperation.StringGetHot,
        RedisThroughputOperation.StringGetExHot,
        RedisThroughputOperation.JsonGetHot,
        RedisThroughputOperation.ListIndexHot,
        RedisThroughputOperation.ListRPopRecycleHot
    ];

    private static readonly RedisThroughputOperation[] QuickOperations =
    [
        RedisThroughputOperation.StringSetGet50_50,
        RedisThroughputOperation.StringGetHot
    ];

    private static readonly int[] FullPayloadSizes = [256, 1024, 4096, 16384];
    private static readonly int[] QuickPayloadSizes = [256, 4096];
    private static readonly int[] FullConcurrency = [32, 64, 128];
    private static readonly int[] QuickConcurrency = [32, 64];
    private static readonly int[] FullPipelineDepth = [4, 8, 16, 32];
    private static readonly int[] QuickPipelineDepth = [8, 16];
    private static readonly int[] FullMuxConnections = [2, 4, 8, 16];
    private static readonly int[] QuickMuxConnections = [2, 4];
    private static readonly bool[] WorkerModes = [false, true];
    private static readonly VapeReadPath[] FullReadPaths = [VapeReadPath.Lease, VapeReadPath.Materialized];
    private static readonly VapeReadPath[] QuickReadPaths = [VapeReadPath.Lease];

    [ParamsSource(nameof(Operations))]
    public RedisThroughputOperation Operation { get; set; }

    [ParamsSource(nameof(PayloadSizes))]
    public int PayloadBytes { get; set; }

    [ParamsSource(nameof(ConcurrencyLevels))]
    public int Concurrency { get; set; }

    [ParamsSource(nameof(PipelineDepths))]
    public int PipelineDepth { get; set; }

    [ParamsSource(nameof(MuxConnectionCounts))]
    public int MuxConnections { get; set; }

    [ParamsSource(nameof(DedicatedLaneWorkerModes))]
    public bool UseDedicatedLaneWorkers { get; set; }

    [ParamsSource(nameof(ReadPaths))]
    public VapeReadPath ReadPath { get; set; }

    public IEnumerable<RedisThroughputOperation> Operations =>
        BenchmarkRedisConfig.ResolveEnumParams("VAPECACHE_BENCH_THROUGHPUT_OPERATIONS", FullOperations, QuickOperations);

    public IEnumerable<int> PayloadSizes =>
        BenchmarkRedisConfig.ResolveIntParamsWithFallback(
            "VAPECACHE_BENCH_THROUGHPUT_PAYLOADS",
            "VAPECACHE_BENCH_PARITY_PAYLOADS",
            FullPayloadSizes,
            QuickPayloadSizes);

    public IEnumerable<int> ConcurrencyLevels =>
        BenchmarkRedisConfig.ResolveIntParams("VAPECACHE_BENCH_THROUGHPUT_CONCURRENCY", FullConcurrency, QuickConcurrency);

    public IEnumerable<int> PipelineDepths =>
        BenchmarkRedisConfig.ResolveIntParams("VAPECACHE_BENCH_THROUGHPUT_PIPELINE_DEPTH", FullPipelineDepth, QuickPipelineDepth);

    public IEnumerable<int> MuxConnectionCounts =>
        BenchmarkRedisConfig.ResolveIntParams("VAPECACHE_BENCH_THROUGHPUT_CONNECTIONS", FullMuxConnections, QuickMuxConnections);

    public IEnumerable<bool> DedicatedLaneWorkerModes =>
        BenchmarkRedisConfig.ResolveBoolParams("VAPECACHE_BENCH_THROUGHPUT_DEDICATED_WORKERS", WorkerModes, WorkerModes);

    public IEnumerable<VapeReadPath> ReadPaths =>
        BenchmarkRedisConfig.ResolveEnumParams(
            "VAPECACHE_BENCH_THROUGHPUT_READ_PATHS",
            FullReadPaths,
            QuickReadPaths);

    private readonly Consumer _consumer = new();

    private ConnectionMultiplexer _serMux = null!;
    private IDatabase _serDb = null!;
    private RedisCommandExecutor _executor = null!;

    private byte[] _payload = Array.Empty<byte>();
    private string[] _stringKeys = Array.Empty<string>();
    private string[] _hashKeys = Array.Empty<string>();
    private string[] _jsonKeys = Array.Empty<string>();
    private string[] _listKeys = Array.Empty<string>();
    private object[][] _jsonGetArgs = Array.Empty<object[]>();
    private string _jsonPayloadText = string.Empty;
    private int _totalOperationsPerInvocation;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var options = BenchmarkRedisConfig.Load();

        _payload = GC.AllocateUninitializedArray<byte>(PayloadBytes);
        BenchmarkRedisConfig.FillPayload(_payload, seed: 3000 + PayloadBytes + Concurrency + PipelineDepth);

        var keySpace = Math.Max(4096, Concurrency * PipelineDepth * 8);
        var keyPrefix = "bench:throughput:" + Guid.NewGuid().ToString("N");
        _stringKeys = new string[keySpace];
        _hashKeys = new string[keySpace];
        _jsonKeys = new string[keySpace];
        _listKeys = new string[keySpace];
        _jsonGetArgs = new object[keySpace][];
        _jsonPayloadText = BuildJsonPayload(PayloadBytes);
        for (var i = 0; i < keySpace; i++)
        {
            _stringKeys[i] = $"{keyPrefix}:s:{i}";
            _hashKeys[i] = $"{keyPrefix}:h:{i}";
            _jsonKeys[i] = $"{keyPrefix}:j:{i}";
            _listKeys[i] = $"{keyPrefix}:l:{i}";
            _jsonGetArgs[i] = [_jsonKeys[i], "."];
        }

        _totalOperationsPerInvocation = ResolvePositiveInt(
            "VAPECACHE_BENCH_THROUGHPUT_TOTAL_OPS",
            DefaultTotalOperationsPerInvocation);

        var muxConnections = Math.Max(1, MuxConnections);
        var maxInFlight = ResolvePositiveInt(
            "VAPECACHE_BENCH_THROUGHPUT_MAX_INFLIGHT",
            Math.Max(2048, Concurrency * PipelineDepth * 4));

        _serMux = await BenchmarkRedisConfig.ConnectStackExchangeAsync(options).ConfigureAwait(false);
        _serDb = _serMux.GetDatabase(options.Database);
        _executor = BenchmarkRedisConfig.CreateVapeCacheExecutor(
            options,
            connections: muxConnections,
            maxInFlight: maxInFlight,
            enableInstrumentation: false,
            enableCoalescedWrites: true,
            useDedicatedLaneWorkers: UseDedicatedLaneWorkers);

        var preloadCount = Math.Min(_stringKeys.Length, Math.Max(2048, Concurrency * PipelineDepth * 2));
        for (var i = 0; i < preloadCount; i++)
        {
            await _serDb.StringSetAsync(_stringKeys[i], _payload).ConfigureAwait(false);
            await _serDb.HashSetAsync(_hashKeys[i], HashField, _payload).ConfigureAwait(false);
            _ = await _serDb.ExecuteAsync("JSON.SET", _jsonKeys[i], ".", _jsonPayloadText).ConfigureAwait(false);
            for (var j = 0; j < 4; j++)
                await _serDb.ListLeftPushAsync(_listKeys[i], _payload).ConfigureAwait(false);
        }
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        try
        {
            if (_serDb is not null)
            {
                await DeleteKeysAsync(_serDb, _stringKeys).ConfigureAwait(false);
                await DeleteKeysAsync(_serDb, _hashKeys).ConfigureAwait(false);
                await DeleteKeysAsync(_serDb, _jsonKeys).ConfigureAwait(false);
                await DeleteKeysAsync(_serDb, _listKeys).ConfigureAwait(false);
            }
        }
        catch
        {
            // best-effort cleanup for benchmark keys
        }

        if (_executor is not null)
            await _executor.DisposeAsync().ConfigureAwait(false);

        try { _serMux?.Dispose(); } catch { }
    }

    [BenchmarkCategory("RedisThroughputHeadToHead")]
    [Benchmark(Baseline = true)]
    public async Task StackExchange()
    {
        var maxOutstanding = Math.Max(1, Concurrency * PipelineDepth);
        var pending = new Task<bool>[maxOutstanding];
        var scheduled = 0;

        for (var opIndex = 0; opIndex < _totalOperationsPerInvocation; opIndex++)
        {
            pending[scheduled++] = IssueStackExchangeOperationAsync(opIndex);
            if (scheduled == maxOutstanding)
            {
                await DrainStackExchangeAsync(pending, scheduled).ConfigureAwait(false);
                scheduled = 0;
            }
        }

        if (scheduled > 0)
            await DrainStackExchangeAsync(pending, scheduled).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("RedisThroughputHeadToHead")]
    public async Task VapeCache()
    {
        var maxOutstanding = Math.Max(1, Concurrency * PipelineDepth);
        var pending = new ValueTask<bool>[maxOutstanding];
        var scheduled = 0;

        for (var opIndex = 0; opIndex < _totalOperationsPerInvocation; opIndex++)
        {
            pending[scheduled++] = IssueVapeCacheOperationAsync(opIndex);
            if (scheduled == maxOutstanding)
            {
                await DrainVapeCacheAsync(pending, scheduled).ConfigureAwait(false);
                scheduled = 0;
            }
        }

        if (scheduled > 0)
            await DrainVapeCacheAsync(pending, scheduled).ConfigureAwait(false);
    }

    private Task<bool> IssueStackExchangeOperationAsync(int opIndex)
    {
        var keyIndex = opIndex % _stringKeys.Length;
        var stringKey = _stringKeys[keyIndex];
        var hashKey = _hashKeys[keyIndex];
        var listKey = _listKeys[keyIndex];

        return Operation switch
        {
            RedisThroughputOperation.StringSetGet50_50 => (opIndex & 1) == 0
                ? _serDb.StringSetAsync(stringKey, _payload)
                : SerStringGetAsync(stringKey),
            RedisThroughputOperation.HashSetGet50_50 => (opIndex & 1) == 0
                ? _serDb.HashSetAsync(hashKey, HashField, _payload)
                : SerHashGetAsync(hashKey),
            RedisThroughputOperation.StringGetHot => SerStringGetAsync(stringKey),
            RedisThroughputOperation.StringGetExHot => SerStringGetExAsync(stringKey),
            RedisThroughputOperation.JsonGetHot => SerJsonGetAsync(keyIndex),
            RedisThroughputOperation.ListIndexHot => SerListIndexAsync(listKey),
            RedisThroughputOperation.ListRPopRecycleHot => SerListRPopRecycleAsync(listKey),
            _ => throw new InvalidOperationException($"Unsupported benchmark operation: {Operation}.")
        };
    }

    private ValueTask<bool> IssueVapeCacheOperationAsync(int opIndex)
    {
        var keyIndex = opIndex % _stringKeys.Length;
        var stringKey = _stringKeys[keyIndex];
        var hashKey = _hashKeys[keyIndex];
        var jsonKey = _jsonKeys[keyIndex];
        var listKey = _listKeys[keyIndex];

        return Operation switch
        {
            RedisThroughputOperation.StringSetGet50_50 => (opIndex & 1) == 0
                ? _executor.SetAsync(stringKey, _payload, ttl: null, CancellationToken.None)
                : GetStringFromVapeCacheAsync(stringKey),
            RedisThroughputOperation.HashSetGet50_50 => (opIndex & 1) == 0
                ? SetHashFromVapeCacheAsync(hashKey)
                : GetHashFromVapeCacheAsync(hashKey),
            RedisThroughputOperation.StringGetHot => GetStringFromVapeCacheAsync(stringKey),
            RedisThroughputOperation.StringGetExHot => GetStringExFromVapeCacheAsync(stringKey),
            RedisThroughputOperation.JsonGetHot => GetJsonFromVapeCacheAsync(jsonKey),
            RedisThroughputOperation.ListIndexHot => GetListIndexFromVapeCacheAsync(listKey),
            RedisThroughputOperation.ListRPopRecycleHot => GetListRPopRecycleFromVapeCacheAsync(listKey),
            _ => throw new InvalidOperationException($"Unsupported benchmark operation: {Operation}.")
        };
    }

    private async Task DrainStackExchangeAsync(Task<bool>[] pending, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var ok = await pending[i].ConfigureAwait(false);
            _consumer.Consume(ok);
        }
    }

    private async Task DrainVapeCacheAsync(ValueTask<bool>[] pending, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var ok = await pending[i].ConfigureAwait(false);
            _consumer.Consume(ok);
        }
    }

    private async Task<bool> SerStringGetAsync(string key)
        => (await _serDb.StringGetAsync(key).ConfigureAwait(false)).HasValue;

    private async Task<bool> SerStringGetExAsync(string key)
        => (await _serDb.StringGetSetExpiryAsync(key, TimeSpan.FromMinutes(2)).ConfigureAwait(false)).HasValue;

    private async Task<bool> SerHashGetAsync(string key)
        => (await _serDb.HashGetAsync(key, HashField).ConfigureAwait(false)).HasValue;

    private async Task<bool> SerJsonGetAsync(int keyIndex)
        => !(await _serDb.ExecuteAsync("JSON.GET", _jsonGetArgs[keyIndex]).ConfigureAwait(false)).IsNull;

    private async Task<bool> SerListIndexAsync(string key)
        => (await _serDb.ListGetByIndexAsync(key, 0).ConfigureAwait(false)).HasValue;

    private async Task<bool> SerListRPopRecycleAsync(string key)
    {
        var value = await _serDb.ListRightPopAsync(key).ConfigureAwait(false);
        if (!value.HasValue)
            return false;

        _ = await _serDb.ListLeftPushAsync(key, value).ConfigureAwait(false);
        return true;
    }

    private async ValueTask<bool> GetStringFromVapeCacheAsync(string key)
    {
        if (ReadPath == VapeReadPath.Lease)
        {
            using var lease = await _executor.GetLeaseAsync(key, CancellationToken.None).ConfigureAwait(false);
            return !lease.IsNull;
        }

        var bytes = await _executor.GetAsync(key, CancellationToken.None).ConfigureAwait(false);
        return bytes is not null;
    }

    private async ValueTask<bool> GetStringExFromVapeCacheAsync(string key)
    {
        if (ReadPath == VapeReadPath.Lease)
        {
            using var lease = await _executor.GetExLeaseAsync(key, TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
            return !lease.IsNull;
        }

        var bytes = await _executor.GetExAsync(key, TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
        return bytes is not null;
    }

    private async ValueTask<bool> GetHashFromVapeCacheAsync(string key)
    {
        if (ReadPath == VapeReadPath.Lease)
        {
            using var lease = await _executor.HGetLeaseAsync(key, HashField, CancellationToken.None).ConfigureAwait(false);
            return !lease.IsNull;
        }

        var bytes = await _executor.HGetAsync(key, HashField, CancellationToken.None).ConfigureAwait(false);
        return bytes is not null;
    }

    private async ValueTask<bool> GetJsonFromVapeCacheAsync(string key)
    {
        if (ReadPath == VapeReadPath.Lease)
        {
            using var lease = await _executor.JsonGetLeaseAsync(key, ".", CancellationToken.None).ConfigureAwait(false);
            return !lease.IsNull;
        }

        var bytes = await _executor.JsonGetAsync(key, ".", CancellationToken.None).ConfigureAwait(false);
        return bytes is not null;
    }

    private async ValueTask<bool> GetListIndexFromVapeCacheAsync(string key)
    {
        var bytes = await _executor.LIndexAsync(key, 0, CancellationToken.None).ConfigureAwait(false);
        return bytes is not null;
    }

    private async ValueTask<bool> GetListRPopRecycleFromVapeCacheAsync(string key)
    {
        if (ReadPath == VapeReadPath.Lease)
        {
            using var lease = await _executor.RPopLeaseAsync(key, CancellationToken.None).ConfigureAwait(false);
            if (lease.IsNull)
                return false;

            _ = await _executor.LPushAsync(key, lease.Memory, CancellationToken.None).ConfigureAwait(false);
            return true;
        }

        var bytes = await _executor.RPopAsync(key, CancellationToken.None).ConfigureAwait(false);
        if (bytes is null)
            return false;

        _ = await _executor.LPushAsync(key, bytes, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private async ValueTask<bool> SetHashFromVapeCacheAsync(string key)
    {
        _ = await _executor.HSetAsync(key, HashField, _payload, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private static async Task DeleteKeysAsync(IDatabase db, IReadOnlyList<string> keys)
    {
        const int ChunkSize = 256;
        var buffer = new RedisKey[ChunkSize];

        for (var offset = 0; offset < keys.Count; offset += ChunkSize)
        {
            var count = Math.Min(ChunkSize, keys.Count - offset);
            for (var i = 0; i < count; i++)
                buffer[i] = keys[offset + i];

            await db.KeyDeleteAsync(buffer.AsSpan(0, count).ToArray()).ConfigureAwait(false);
        }
    }

    private static int ResolvePositiveInt(string key, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        return int.TryParse(raw, out var value) && value > 0
            ? value
            : fallback;
    }

    private static string BuildJsonPayload(int payloadBytes)
    {
        var bodyLength = Math.Max(1, payloadBytes - 8);
        return $"{{\"v\":\"{new string('x', bodyLength)}\"}}";
    }

    public enum RedisThroughputOperation
    {
        StringSetGet50_50,
        HashSetGet50_50,
        StringGetHot,
        StringGetExHot,
        JsonGetHot,
        ListIndexHot,
        ListRPopRecycleHot
    }

    public enum VapeReadPath
    {
        Lease,
        Materialized
    }
}
