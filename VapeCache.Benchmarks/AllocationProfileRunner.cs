using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text;
using System.Threading;
using VapeCache.Abstractions.Connections;
using StackExchange.Redis;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks;

internal static class AllocationProfileRunner
{
    public static async Task RunAsync(string[] args)
    {
        var opName = args.Length > 1 ? args[1] : "BfAdd";
        var iterations = args.Length > 2 && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iters) ? iters : 10000;
        var warmup = args.Length > 3 && int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var warm) ? warm : 1000;
        var top = args.Length > 4 && int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : 15;

        Console.WriteLine($"Allocation profiling: {opName} (warmup {warmup}, iterations {iterations})");

        var options = BenchmarkRedisConfig.Load();
        var executor = BenchmarkRedisConfig.CreateVapeCacheExecutor(options, enableInstrumentation: false);
        var cleanupMux = await BenchmarkRedisConfig.ConnectStackExchangeAsync(options).ConfigureAwait(false);
        var cleanupDb = cleanupMux.GetDatabase(options.Database);

        var scope = new ModuleBenchmarkScope();
        await scope.SetupAsync(executor).ConfigureAwait(false);

        try
        {
            var op = ResolveOperation(opName, scope, executor);
            if (op is null)
                throw new InvalidOperationException($"Unknown operation '{opName}'.");

            // Warmup before profiling to avoid one-time allocations.
            await RunLoopAsync(op, warmup).ConfigureAwait(false);

            using var profiler = new AllocationProfiler();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var beforeTotal = GC.GetTotalAllocatedBytes(true);
            profiler.Start();
            await RunLoopAsync(op, iterations).ConfigureAwait(false);
            profiler.Stop();
            var afterTotal = GC.GetTotalAllocatedBytes(true);

            var totalAllocated = afterTotal - beforeTotal;
            var perOp = iterations > 0 ? (double)totalAllocated / iterations : 0;

            Console.WriteLine($"Total allocated bytes (process): {totalAllocated:N0} ({perOp:N1} bytes/op)");

            var topAllocations = profiler.GetTopAllocations(top);
            if (topAllocations.Count == 0)
            {
                Console.WriteLine("No GCAllocationTick samples were captured; consider increasing iterations.");
            }
            else
            {
                Console.WriteLine("Top allocations (sampled):");
                foreach (var entry in topAllocations)
                {
                    Console.WriteLine($"  {entry.TypeName} - {entry.Bytes:N0} bytes");
                }
            }
        }
        finally
        {
            await scope.CleanupAsync(cleanupDb).ConfigureAwait(false);
            await executor.DisposeAsync().ConfigureAwait(false);
            cleanupMux.Dispose();
        }
    }

    private static Func<int, Task>? ResolveOperation(string name, ModuleBenchmarkScope scope, RedisCommandExecutor executor)
    {
        return name.ToLowerInvariant() switch
        {
            "bfadd" => async _ => await executor.BfAddAsync(scope.BloomKey, scope.BloomItem, CancellationToken.None).ConfigureAwait(false),
            "bfexists" => async _ => await executor.BfExistsAsync(scope.BloomKey, scope.BloomExistsItem, CancellationToken.None).ConfigureAwait(false),
            "ftsearch" => async _ => await executor.FtSearchAsync(scope.SearchIndex, "*", 0, 10, CancellationToken.None).ConfigureAwait(false),
            "jsonget" => async _ => await executor.JsonGetAsync(scope.JsonKey, ".", CancellationToken.None).ConfigureAwait(false),
            "jsongetlease" => async _ =>
            {
                var lease = await executor.JsonGetLeaseAsync(scope.JsonKey, ".", CancellationToken.None).ConfigureAwait(false);
                lease.Dispose();
            },
            "jsonset" => async _ => await executor.JsonSetAsync(scope.JsonKey, ".", scope.JsonPayload, CancellationToken.None).ConfigureAwait(false),
            "jsonsetlease" => async _ => await executor.JsonSetLeaseAsync(scope.JsonKey, ".", scope.JsonPayloadLease, CancellationToken.None).ConfigureAwait(false),
            "tsadd" => async _ =>
            {
                var ts = scope.NextTimestamp();
                await executor.TsAddAsync(scope.TimeSeriesKey, ts, 1, CancellationToken.None).ConfigureAwait(false);
            },
            "tsrange" => async _ =>
            {
                var end = scope.CurrentTimestamp;
                await executor.TsRangeAsync(scope.TimeSeriesKey, end - 1000, end, CancellationToken.None).ConfigureAwait(false);
            },
            _ => null
        };
    }

    private static async Task RunLoopAsync(Func<int, Task> op, int iterations)
    {
        for (var i = 0; i < iterations; i++)
            await op(i).ConfigureAwait(false);
    }

    private sealed class AllocationProfiler : EventListener
    {
        private readonly Dictionary<string, long> _allocations = new(StringComparer.Ordinal);
        private bool _recording;

        public void Start() => _recording = true;
        public void Stop() => _recording = false;

        public IReadOnlyList<(string TypeName, long Bytes)> GetTopAllocations(int top)
            => _allocations
                .OrderByDescending(pair => pair.Value)
                .Take(top)
                .Select(pair => (pair.Key, pair.Value))
                .ToList();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "System.Runtime")
                EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (!_recording || !string.Equals(eventData.EventName, "GCAllocationTick", StringComparison.Ordinal))
                return;

            var typeName = GetPayloadValue<string>(eventData, "TypeName") ?? "Unknown";
            var size = GetPayloadValue<long>(eventData, "AllocationAmount");
            if (size == 0)
                return;

            if (_allocations.TryGetValue(typeName, out var current))
                _allocations[typeName] = current + size;
            else
                _allocations[typeName] = size;
        }

        private static T? GetPayloadValue<T>(EventWrittenEventArgs args, string name)
        {
            if (args.PayloadNames is null || args.Payload is null)
                return default;

            for (var i = 0; i < args.PayloadNames.Count; i++)
            {
                if (!string.Equals(args.PayloadNames[i], name, StringComparison.Ordinal))
                    continue;

                var value = args.Payload[i];
                if (value is null)
                    return default;

                if (value is T matched)
                    return matched;

                try
                {
                    return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
                }
                catch
                {
                    return default;
                }
            }

            return default;
        }
    }

    private sealed class ModuleBenchmarkScope
    {
        private readonly byte[] _bloomItem = new byte[16];
        private readonly byte[] _bloomExistsItem = new byte[16];
        private long _tsCursor;

        public string JsonKey { get; private set; } = string.Empty;
        public string SearchIndex { get; private set; } = string.Empty;
        public string SearchPrefix { get; private set; } = string.Empty;
        public string DocKey { get; private set; } = string.Empty;
        public string BloomKey { get; private set; } = string.Empty;
        public string TimeSeriesKey { get; private set; } = string.Empty;
        public ReadOnlyMemory<byte> JsonPayload { get; private set; }
        public RedisValueLease JsonPayloadLease { get; private set; }
        public ReadOnlyMemory<byte> BloomItem => _bloomItem;
        public ReadOnlyMemory<byte> BloomExistsItem => _bloomExistsItem;
        public long CurrentTimestamp => Volatile.Read(ref _tsCursor);

        public async Task SetupAsync(RedisCommandExecutor executor)
        {
            var payload = new string('a', 1024);
            JsonPayload = Encoding.UTF8.GetBytes($"{{\"name\":\"alloc-profile\",\"payload\":\"{payload}\"}}");

            var prefix = "bench:alloc:" + Guid.NewGuid().ToString("N");
            JsonKey = prefix + ":json";
            SearchIndex = prefix + ":idx";
            SearchPrefix = prefix + ":doc:";
            DocKey = SearchPrefix + "1";
            BloomKey = prefix + ":bf";
            TimeSeriesKey = prefix + ":ts";

            var modules = await executor.ModuleListAsync(CancellationToken.None).ConfigureAwait(false);
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
                    "Allocation profiling requires: " + string.Join(", ", missing) +
                    ". Install the modules or choose non-module ops.");
            }

            await executor.JsonSetAsync(JsonKey, ".", JsonPayload, CancellationToken.None).ConfigureAwait(false);
            JsonPayloadLease = await executor.JsonGetLeaseAsync(JsonKey, ".", CancellationToken.None).ConfigureAwait(false);
            if (JsonPayloadLease.IsNull)
                throw new InvalidOperationException("Failed to read JSON payload lease for profiling.");
            await executor.FtCreateAsync(SearchIndex, SearchPrefix, new[] { "title", "body" }, CancellationToken.None).ConfigureAwait(false);
            await executor.HSetAsync(DocKey, "title", "bench"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
            await executor.HSetAsync(DocKey, "body", "bench body text"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
            await executor.FtSearchAsync(SearchIndex, "*", 0, 1, CancellationToken.None).ConfigureAwait(false);

            Random.Shared.NextBytes(_bloomItem);
            Array.Copy(_bloomItem, _bloomExistsItem, _bloomItem.Length);
            await executor.BfAddAsync(BloomKey, _bloomExistsItem, CancellationToken.None).ConfigureAwait(false);

            _tsCursor = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await executor.TsCreateAsync(TimeSeriesKey, CancellationToken.None).ConfigureAwait(false);
            await executor.TsAddAsync(TimeSeriesKey, _tsCursor, 1, CancellationToken.None).ConfigureAwait(false);
        }

        public long NextTimestamp() => Interlocked.Increment(ref _tsCursor);

        public async Task CleanupAsync(IDatabase cleanupDb)
        {
            try { await cleanupDb.ExecuteAsync("FT.DROPINDEX", SearchIndex, "DD").ConfigureAwait(false); } catch { }
            try { await cleanupDb.KeyDeleteAsync(JsonKey).ConfigureAwait(false); } catch { }
            try { await cleanupDb.KeyDeleteAsync(BloomKey).ConfigureAwait(false); } catch { }
            try { await cleanupDb.KeyDeleteAsync(TimeSeriesKey).ConfigureAwait(false); } catch { }
            JsonPayloadLease.Dispose();
        }

        private static bool HasModule(string[] modules, params string[] names)
            => modules.Any(module => names.Any(name => string.Equals(module, name, StringComparison.OrdinalIgnoreCase)));
    }
}
