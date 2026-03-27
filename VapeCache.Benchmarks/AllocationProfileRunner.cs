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
    [Flags]
    private enum AllocationProfileModules
    {
        None = 0,
        Json = 1,
        Search = 2,
        Bloom = 4,
        TimeSeries = 8,
    }

    public static async Task RunAsync(string[] args)
    {
        var opName = args.Length > 1 ? args[1] : "BfAdd";
        var iterations = args.Length > 2 && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iters) ? iters : 10000;
        var warmup = args.Length > 3 && int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var warm) ? warm : 1000;
        var top = args.Length > 4 && int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : 15;

        Console.Out.WriteLine($"Allocation profiling: {opName} (warmup {warmup}, iterations {iterations})");

        var options = BenchmarkRedisConfig.Load();
        var executor = BenchmarkRedisConfig.CreateVapeCacheExecutor(options, enableInstrumentation: false);
        var cleanupMux = await BenchmarkRedisConfig.ConnectStackExchangeAsync(options).ConfigureAwait(false);
        var cleanupDb = cleanupMux.GetDatabase(options.Database);

        var normalizedOpName = opName.ToLowerInvariant();
        var requiredModules = GetRequiredModules(normalizedOpName);
        var scope = new ModuleBenchmarkScope();
        await scope.SetupAsync(executor, requiredModules).ConfigureAwait(false);

        try
        {
            var op = ResolveOperation(normalizedOpName, scope, executor);
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

            Console.Out.WriteLine($"Total allocated bytes (process): {totalAllocated:N0} ({perOp:N1} bytes/op)");

            var topAllocations = profiler.GetTopAllocations(top);
            if (topAllocations.Count == 0)
            {
                Console.Out.WriteLine("No GCAllocationTick samples were captured; consider increasing iterations.");
            }
            else
            {
                Console.Out.WriteLine("Top allocations (sampled):");
                foreach (var entry in topAllocations)
                {
                    Console.Out.WriteLine($"  {entry.TypeName} - {entry.Bytes:N0} bytes");
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

    private static AllocationProfileModules GetRequiredModules(string normalizedOperationName)
    {
        return normalizedOperationName switch
        {
            "bfadd" or "bfexists" => AllocationProfileModules.Bloom,
            "ftsearch" or "ftsearchcount" => AllocationProfileModules.Search,
            "jsonget" or "jsongetlease" or "jsonset" or "jsonsetlease" => AllocationProfileModules.Json,
            "tsadd" or "tsrange" or "tsrangecount" => AllocationProfileModules.TimeSeries,
            "modulelist" => AllocationProfileModules.Json | AllocationProfileModules.Search | AllocationProfileModules.Bloom | AllocationProfileModules.TimeSeries,
            _ => AllocationProfileModules.None,
        };
    }

    private static Func<int, Task>? ResolveOperation(string name, ModuleBenchmarkScope scope, RedisCommandExecutor executor)
    {
        return name switch
        {
            "get" => async _ => await executor.GetAsync(scope.StringKey, CancellationToken.None).ConfigureAwait(false),
            "getlease" => async _ =>
            {
                var lease = await executor.GetLeaseAsync(scope.StringKey, CancellationToken.None).ConfigureAwait(false);
                lease.Dispose();
            },
            "getex" => async _ => await executor.GetExAsync(scope.StringKey, TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false),
            "getexlease" => async _ =>
            {
                var lease = await executor.GetExLeaseAsync(scope.StringKey, TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
                lease.Dispose();
            },
            "getrange" => async _ => await executor.GetRangeAsync(scope.RangeKey, 0, 0, CancellationToken.None).ConfigureAwait(false),
            "mgetcount" => async _ => await executor.MGetCountAsync(scope.BatchKeys, CancellationToken.None).ConfigureAwait(false),
            "hget" => async _ => await executor.HGetAsync(scope.HashKey, scope.HashShopperField, CancellationToken.None).ConfigureAwait(false),
            "hgetlease" => async _ =>
            {
                var lease = await executor.HGetLeaseAsync(scope.HashKey, scope.HashShopperField, CancellationToken.None).ConfigureAwait(false);
                lease.Dispose();
            },
            "hmgetcount" => async _ => await executor.HMGetCountAsync(scope.HashKey, scope.HashFields, CancellationToken.None).ConfigureAwait(false),
            "lindex" => async _ => await executor.LIndexAsync(scope.ListKey, 0, CancellationToken.None).ConfigureAwait(false),
            "lrangecount" => async _ => await executor.LRangeCountAsync(scope.ListKey, 0, -1, CancellationToken.None).ConfigureAwait(false),
            "llen" => async _ => await executor.LLenAsync(scope.ListKey, CancellationToken.None).ConfigureAwait(false),
            "sismember" => async _ => await executor.SIsMemberAsync(scope.SetKey, scope.SetMember, CancellationToken.None).ConfigureAwait(false),
            "smemberscount" => async _ => await executor.SMembersCountAsync(scope.SetKey, CancellationToken.None).ConfigureAwait(false),
            "scard" => async _ => await executor.SCardAsync(scope.SetKey, CancellationToken.None).ConfigureAwait(false),
            "zscore" => async _ => await executor.ZScoreAsync(scope.SortedSetKey, scope.SortedSetMemberA, CancellationToken.None).ConfigureAwait(false),
            "zrank" => async _ => await executor.ZRankAsync(scope.SortedSetKey, scope.SortedSetMemberB, descending: false, CancellationToken.None).ConfigureAwait(false),
            "zrangewithscorescount" => async _ => await executor.ZRangeWithScoresCountAsync(scope.SortedSetKey, 0, -1, descending: false, CancellationToken.None).ConfigureAwait(false),
            "zrangebyscorewithscorescount" => async _ => await executor.ZRangeByScoreWithScoresCountAsync(scope.SortedSetKey, double.NegativeInfinity, double.PositiveInfinity, descending: false, offset: 0, count: 10, CancellationToken.None).ConfigureAwait(false),
            "zcard" => async _ => await executor.ZCardAsync(scope.SortedSetKey, CancellationToken.None).ConfigureAwait(false),
            "ping" => async _ => await executor.PingAsync(CancellationToken.None).ConfigureAwait(false),
            "modulelist" => async _ => await executor.ModuleListAsync(CancellationToken.None).ConfigureAwait(false),
            "bfadd" => async _ => await executor.BfAddAsync(scope.BloomKey, scope.BloomItem, CancellationToken.None).ConfigureAwait(false),
            "bfexists" => async _ => await executor.BfExistsAsync(scope.BloomKey, scope.BloomExistsItem, CancellationToken.None).ConfigureAwait(false),
            "ftsearch" => async _ => await executor.FtSearchAsync(scope.SearchIndex, "*", 0, 10, CancellationToken.None).ConfigureAwait(false),
            "ftsearchcount" => async _ => await executor.FtSearchCountAsync(scope.SearchIndex, "*", 0, 0, CancellationToken.None).ConfigureAwait(false),
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
            "tsrangecount" => async _ =>
            {
                var end = scope.CurrentTimestamp;
                await executor.TsRangeCountAsync(scope.TimeSeriesKey, end - 1000, end, CancellationToken.None).ConfigureAwait(false);
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
        public string StringKey { get; private set; } = string.Empty;
        public string RangeKey { get; private set; } = string.Empty;
        public string BatchKeyA { get; private set; } = string.Empty;
        public string BatchKeyB { get; private set; } = string.Empty;
        public string[] BatchKeys { get; private set; } = Array.Empty<string>();
        public string HashKey { get; private set; } = string.Empty;
        public string HashShopperField { get; } = "shopper";
        public string[] HashFields { get; private set; } = Array.Empty<string>();
        public string ListKey { get; private set; } = string.Empty;
        public string SetKey { get; private set; } = string.Empty;
        public string SortedSetKey { get; private set; } = string.Empty;
        public ReadOnlyMemory<byte> JsonPayload { get; private set; }
        public RedisValueLease JsonPayloadLease { get; private set; } = RedisValueLease.Null;
        public ReadOnlyMemory<byte> BloomItem => _bloomItem;
        public ReadOnlyMemory<byte> BloomExistsItem => _bloomExistsItem;
        public ReadOnlyMemory<byte> StringValue { get; private set; }
        public ReadOnlyMemory<byte> RangeValue { get; private set; }
        public ReadOnlyMemory<byte> BatchValueA { get; private set; }
        public ReadOnlyMemory<byte> BatchValueB { get; private set; }
        public ReadOnlyMemory<byte> SetMember { get; private set; }
        public ReadOnlyMemory<byte> SortedSetMemberA { get; private set; }
        public ReadOnlyMemory<byte> SortedSetMemberB { get; private set; }
        public long CurrentTimestamp => Volatile.Read(ref _tsCursor);

        public async Task SetupAsync(RedisCommandExecutor executor, AllocationProfileModules requiredModules)
        {
            var payload = new string('a', 1024);
            JsonPayload = Encoding.UTF8.GetBytes($"{{\"name\":\"alloc-profile\",\"payload\":\"{payload}\"}}");
            StringValue = Encoding.UTF8.GetBytes("alloc-profile-value");
            RangeValue = Encoding.UTF8.GetBytes("0123456789abcdef");
            BatchValueA = Encoding.UTF8.GetBytes("batch-a");
            BatchValueB = Encoding.UTF8.GetBytes("batch-b");
            SetMember = Encoding.UTF8.GetBytes("member-a");
            SortedSetMemberA = Encoding.UTF8.GetBytes("z-member-a");
            SortedSetMemberB = Encoding.UTF8.GetBytes("z-member-b");

            var prefix = "bench:alloc:" + Guid.NewGuid().ToString("N");
            JsonKey = prefix + ":json";
            SearchIndex = prefix + ":idx";
            SearchPrefix = prefix + ":doc:";
            DocKey = SearchPrefix + "1";
            BloomKey = prefix + ":bf";
            TimeSeriesKey = prefix + ":ts";
            StringKey = prefix + ":string";
            RangeKey = prefix + ":range";
            BatchKeyA = prefix + ":batch:a";
            BatchKeyB = prefix + ":batch:b";
            BatchKeys = [BatchKeyA, BatchKeyB];
            HashKey = prefix + ":hash";
            HashFields = [HashShopperField, "sale", "product"];
            ListKey = prefix + ":list";
            SetKey = prefix + ":set";
            SortedSetKey = prefix + ":zset";

            var modules = Array.Empty<string>();
            if (requiredModules != AllocationProfileModules.None)
            {
                modules = await executor.ModuleListAsync(CancellationToken.None).ConfigureAwait(false);
            }

            var missing = new List<string>();
            if (requiredModules.HasFlag(AllocationProfileModules.Json) && !HasModule(modules, "ReJSON", "ReJSON-RL"))
                missing.Add("RedisJSON");
            if (requiredModules.HasFlag(AllocationProfileModules.Search) && !HasModule(modules, "search"))
                missing.Add("RediSearch");
            if (requiredModules.HasFlag(AllocationProfileModules.Bloom) && !HasModule(modules, "bf", "bloom"))
                missing.Add("RedisBloom");
            if (requiredModules.HasFlag(AllocationProfileModules.TimeSeries) && !HasModule(modules, "timeseries", "ts"))
                missing.Add("RedisTimeSeries");

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    "Allocation profiling requires: " + string.Join(", ", missing) +
                    ". Install the modules or choose non-module ops.");
            }

            await executor.SetAsync(StringKey, StringValue, TimeSpan.FromMinutes(5), CancellationToken.None).ConfigureAwait(false);
            await executor.SetAsync(RangeKey, RangeValue, TimeSpan.FromMinutes(5), CancellationToken.None).ConfigureAwait(false);
            await executor.MSetAsync([(BatchKeyA, BatchValueA), (BatchKeyB, BatchValueB)], CancellationToken.None).ConfigureAwait(false);
            await executor.HSetAsync(HashKey, HashFields[0], StringValue, CancellationToken.None).ConfigureAwait(false);
            await executor.HSetAsync(HashKey, HashFields[1], BatchValueA, CancellationToken.None).ConfigureAwait(false);
            await executor.HSetAsync(HashKey, HashFields[2], BatchValueB, CancellationToken.None).ConfigureAwait(false);
            await executor.LPushAsync(ListKey, StringValue, CancellationToken.None).ConfigureAwait(false);
            await executor.RPushAsync(ListKey, BatchValueA, CancellationToken.None).ConfigureAwait(false);
            await executor.RPushManyAsync(ListKey, [BatchValueB, RangeValue], 2, CancellationToken.None).ConfigureAwait(false);
            await executor.SAddAsync(SetKey, SetMember, CancellationToken.None).ConfigureAwait(false);
            await executor.SAddAsync(SetKey, BatchValueA, CancellationToken.None).ConfigureAwait(false);
            await executor.ZAddAsync(SortedSetKey, 1d, SortedSetMemberA, CancellationToken.None).ConfigureAwait(false);
            await executor.ZAddAsync(SortedSetKey, 2d, SortedSetMemberB, CancellationToken.None).ConfigureAwait(false);

            Random.Shared.NextBytes(_bloomItem);
            Array.Copy(_bloomItem, _bloomExistsItem, _bloomItem.Length);
            _tsCursor = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (requiredModules.HasFlag(AllocationProfileModules.Json))
            {
                await executor.JsonSetAsync(JsonKey, ".", JsonPayload, CancellationToken.None).ConfigureAwait(false);
                JsonPayloadLease = await executor.JsonGetLeaseAsync(JsonKey, ".", CancellationToken.None).ConfigureAwait(false);
                if (JsonPayloadLease.IsNull)
                    throw new InvalidOperationException("Failed to read JSON payload lease for profiling.");
            }

            if (requiredModules.HasFlag(AllocationProfileModules.Search))
            {
                await executor.FtCreateAsync(SearchIndex, SearchPrefix, new[] { "title", "body" }, CancellationToken.None).ConfigureAwait(false);
                await executor.HSetAsync(DocKey, "title", "bench"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
                await executor.HSetAsync(DocKey, "body", "bench body text"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
                await executor.FtSearchAsync(SearchIndex, "*", 0, 1, CancellationToken.None).ConfigureAwait(false);
            }

            if (requiredModules.HasFlag(AllocationProfileModules.Bloom))
            {
                await executor.BfAddAsync(BloomKey, _bloomExistsItem, CancellationToken.None).ConfigureAwait(false);
            }

            if (requiredModules.HasFlag(AllocationProfileModules.TimeSeries))
            {
                await executor.TsCreateAsync(TimeSeriesKey, CancellationToken.None).ConfigureAwait(false);
                await executor.TsAddAsync(TimeSeriesKey, _tsCursor, 1, CancellationToken.None).ConfigureAwait(false);
            }
        }

        public long NextTimestamp() => Interlocked.Increment(ref _tsCursor);

        public async Task CleanupAsync(IDatabase cleanupDb)
        {
            try { await cleanupDb.ExecuteAsync("FT.DROPINDEX", SearchIndex, "DD").ConfigureAwait(false); } catch { }
            try { await cleanupDb.KeyDeleteAsync(StringKey).ConfigureAwait(false); } catch { }
            try { await cleanupDb.KeyDeleteAsync(RangeKey).ConfigureAwait(false); } catch { }
            try { await cleanupDb.KeyDeleteAsync(BatchKeyA).ConfigureAwait(false); } catch { }
            try { await cleanupDb.KeyDeleteAsync(BatchKeyB).ConfigureAwait(false); } catch { }
            try { await cleanupDb.KeyDeleteAsync(HashKey).ConfigureAwait(false); } catch { }
            try { await cleanupDb.KeyDeleteAsync(ListKey).ConfigureAwait(false); } catch { }
            try { await cleanupDb.KeyDeleteAsync(SetKey).ConfigureAwait(false); } catch { }
            try { await cleanupDb.KeyDeleteAsync(SortedSetKey).ConfigureAwait(false); } catch { }
            try { await cleanupDb.KeyDeleteAsync(JsonKey).ConfigureAwait(false); } catch { }
            try { await cleanupDb.KeyDeleteAsync(BloomKey).ConfigureAwait(false); } catch { }
            try { await cleanupDb.KeyDeleteAsync(TimeSeriesKey).ConfigureAwait(false); } catch { }
            JsonPayloadLease.Dispose();
        }

        private static bool HasModule(string[] modules, params string[] names)
            => modules.Any(module => names.Any(name => string.Equals(module, name, StringComparison.OrdinalIgnoreCase)));
    }
}

