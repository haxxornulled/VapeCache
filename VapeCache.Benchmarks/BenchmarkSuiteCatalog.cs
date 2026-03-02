using System.Collections.Frozen;
using System.Text;

namespace VapeCache.Benchmarks;

public static class BenchmarkSuiteCatalog
{
    private const string HotPathComparisonAlias = "hotpath";
    private const string ReportAudienceEnvironmentVariable = "VAPECACHE_BENCH_REPORT_AUDIENCE";
    private static readonly FrozenSet<string> HotPathComparisonSuites =
        new[] { "client", "throughput", "endtoend" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly BenchmarkSuiteDefinition[] FeatureSuites =
    [
        new(
            "cache",
            BenchmarkSuiteAudience.FeatureSet,
            "Cache API surface, typed collections, and stampede protection",
            ["*CacheServiceApiBenchmarks*", "*TypedCollectionsBenchmarks*", "*StampedeProtectedCacheServiceBenchmarks*"],
            []),
        new(
            "resilience",
            BenchmarkSuiteAudience.FeatureSet,
            "Circuit-breaker and hot-key contention behavior",
            ["*CircuitBreakerPerformanceBenchmarks*", "*CacheHotKeyContentionBenchmarks*"],
            []),
        new(
            "protocol",
            BenchmarkSuiteAudience.FeatureSet,
            "RESP parsing, protocol writes, and socket awaitables",
            ["*RespParserLiteBenchmarks*", "*RedisRespReaderBenchmarks*", "*RedisRespProtocolBenchmarks*", "*RedisRespProtocolWriteBenchmarks*", "*SocketAwaitableBenchmarks*"],
            []),
        new(
            "connections",
            BenchmarkSuiteAudience.FeatureSet,
            "Connection string, pooling, and multiplexed connection internals",
            ["*RedisConnectionStringParserBenchmarks*", "*RedisConnectionPoolBenchmarks*", "*RedisMultiplexedConnectionBenchmarks*"],
            []),
        new(
            "sanity",
            BenchmarkSuiteAudience.FeatureSet,
            "GC and allocator sanity baselines",
            ["*SanityBenchmarks*"],
            [])
    ];

    private static readonly BenchmarkSuiteDefinition[] ComparisonSuites =
    [
        new(
            "client",
            BenchmarkSuiteAudience.Comparison,
            "VapeCache vs StackExchange.Redis client API parity",
            ["*RedisClientHeadToHeadBenchmarks*"],
            [new("VAPECACHE_BENCH_CLIENT_PAYLOADS", "256,1024,4096,16384")]),
        new(
            "throughput",
            BenchmarkSuiteAudience.Comparison,
            "Sustained throughput and tail-latency matrices",
            ["*RedisThroughputHeadToHeadBenchmarks*"],
            [new("VAPECACHE_BENCH_THROUGHPUT_PAYLOADS", "256,1024,4096,16384")]),
        new(
            "endtoend",
            BenchmarkSuiteAudience.Comparison,
            "End-to-end operation latency across common data structures",
            ["*RedisEndToEndHeadToHeadBenchmarks*"],
            [new("VAPECACHE_BENCH_E2E_PAYLOADS", "256,1024,4096,16384")]),
        new(
            "modules",
            BenchmarkSuiteAudience.Comparison,
            "Redis module command comparisons (JSON, Search, Bloom, TimeSeries)",
            ["*RedisModuleHeadToHeadBenchmarks*"],
            [new("VAPECACHE_BENCH_MODULE_JSON_CHARS", "256,1024,4096")]),
        new(
            "datatypes",
            BenchmarkSuiteAudience.Comparison,
            "Strict string/hash/list/set/sorted-set parity on the tuned mux path",
            ["*RedisDatatypeParityHeadToHeadBenchmarks*"],
            [
                new("VAPECACHE_BENCH_DATATYPE_PAYLOADS", "256,1024,4096,16384"),
                new("VAPECACHE_BENCH_DEDICATED_LANE_WORKERS", "true"),
                new("VAPECACHE_BENCH_SOCKET_RESP_READER", "true")
            ])
    ];

    private static readonly FrozenDictionary<string, BenchmarkSuiteDefinition> FeatureLookup = CreateLookup(FeatureSuites);
    private static readonly FrozenDictionary<string, BenchmarkSuiteDefinition> ComparisonLookup = CreateLookup(ComparisonSuites);

    public static bool IsListCommand(string value)
        => string.Equals(value, "list-suites", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "suites", StringComparison.OrdinalIgnoreCase);

    public static bool IsSuiteCommand(string value) => TryParseAudience(value, out _);

    public static IReadOnlyList<BenchmarkSuiteDefinition> GetSuites(BenchmarkSuiteAudience audience)
        => audience == BenchmarkSuiteAudience.FeatureSet ? FeatureSuites : ComparisonSuites;

    public static string FormatCatalog(string? scope = null)
    {
        var builder = new StringBuilder()
            .AppendLine("Benchmark suites")
            .AppendLine("  featuresets <suite|all> [BenchmarkDotNet args]")
            .AppendLine("  compare <suite|all> [BenchmarkDotNet args]")
            .AppendLine("    compare hotpath = compare client + throughput + endtoend")
            .AppendLine("  list-suites [featuresets|compare]");

        builder.AppendLine()
            .AppendLine("Reporting audiences")
            .AppendLine("  hot-path comparison: compare hotpath (or compare client|throughput|endtoend)")
            .AppendLine("  feature/fallback behavior: featuresets cache");

        if (!string.IsNullOrWhiteSpace(scope) && TryParseAudience(scope, out var scopedAudience))
        {
            AppendAudience(builder, scopedAudience);
            return builder.ToString().TrimEnd();
        }

        AppendAudience(builder, BenchmarkSuiteAudience.FeatureSet);
        AppendAudience(builder, BenchmarkSuiteAudience.Comparison);
        return builder.ToString().TrimEnd();
    }

    public static bool TryCreateInvocationPlan(
        string[] args,
        out BenchmarkInvocationPlan plan,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(args);

        plan = null!;
        error = string.Empty;

        if (args.Length == 0 || !TryParseAudience(args[0], out var audience))
        {
            error = "Benchmark suite commands must start with 'featuresets' or 'compare'.";
            return false;
        }

        var suiteKeyIndex = args.Length > 1 && !IsOption(args[1]) ? 1 : -1;
        var suiteKey = suiteKeyIndex >= 0 ? args[suiteKeyIndex] : "all";
        var passthroughIndex = suiteKeyIndex >= 0 ? 2 : 1;

        var selectedSuites = ResolveSuites(audience, suiteKey, out error);
        if (selectedSuites is null)
            return false;

        var filters = selectedSuites
            .SelectMany(static suite => suite.Filters)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var arguments = new List<string>(1 + filters.Length + Math.Max(0, args.Length - passthroughIndex))
        {
            "--filter"
        };
        arguments.AddRange(filters);
        arguments.AddRange(args.Skip(passthroughIndex));

        var environmentDefaults = selectedSuites
            .SelectMany(static suite => suite.EnvironmentDefaults)
            .Append(new KeyValuePair<string, string>(ReportAudienceEnvironmentVariable, ResolveReportAudience(audience, selectedSuites)))
            .GroupBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();

        var displayName = string.Equals(suiteKey, "all", StringComparison.OrdinalIgnoreCase)
            ? $"{GetAudienceDisplayName(audience)} (all)"
            : string.Equals(suiteKey, HotPathComparisonAlias, StringComparison.OrdinalIgnoreCase)
                ? "Comparison: hotpath"
            : selectedSuites[0].DisplayName;

        var reportAudience = ResolveReportAudience(audience, selectedSuites);
        plan = new BenchmarkInvocationPlan(displayName, reportAudience, arguments.ToArray(), environmentDefaults);
        return true;
    }

    private static FrozenDictionary<string, BenchmarkSuiteDefinition> CreateLookup(IEnumerable<BenchmarkSuiteDefinition> suites)
        => suites.ToFrozenDictionary(static suite => suite.Key, StringComparer.OrdinalIgnoreCase);

    private static void AppendAudience(StringBuilder builder, BenchmarkSuiteAudience audience)
    {
        builder.AppendLine()
            .AppendLine($"{GetAudienceDisplayName(audience)}:");

        foreach (var suite in GetSuites(audience))
        {
            builder.AppendLine($"  {suite.Key,-11} {suite.Description}");
        }

        if (audience == BenchmarkSuiteAudience.Comparison)
        {
            builder.AppendLine($"  {HotPathComparisonAlias,-11} Hot-path comparison bundle (client + throughput + endtoend)");
        }
    }

    private static BenchmarkSuiteDefinition[]? ResolveSuites(
        BenchmarkSuiteAudience audience,
        string suiteKey,
        out string error)
    {
        error = string.Empty;
        if (string.Equals(suiteKey, "all", StringComparison.OrdinalIgnoreCase))
            return GetSuites(audience).ToArray();

        if (audience == BenchmarkSuiteAudience.Comparison &&
            string.Equals(suiteKey, HotPathComparisonAlias, StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                ComparisonLookup["client"],
                ComparisonLookup["throughput"],
                ComparisonLookup["endtoend"]
            ];
        }

        if (TryGetSuite(audience, suiteKey, out var suite))
            return [suite];

        error = $"Unknown {GetAudienceDisplayName(audience)} suite '{suiteKey}'.";
        return null;
    }

    private static bool TryGetSuite(BenchmarkSuiteAudience audience, string key, out BenchmarkSuiteDefinition suite)
    {
        var lookup = audience == BenchmarkSuiteAudience.FeatureSet
            ? FeatureLookup
            : ComparisonLookup;

        return lookup.TryGetValue(key, out suite!);
    }

    private static string GetAudienceDisplayName(BenchmarkSuiteAudience audience)
        => audience == BenchmarkSuiteAudience.FeatureSet ? "Feature sets" : "Comparisons";

    private static string ResolveReportAudience(BenchmarkSuiteAudience audience, IReadOnlyList<BenchmarkSuiteDefinition> suites)
    {
        if (audience == BenchmarkSuiteAudience.FeatureSet)
        {
            var includesCache = suites.Any(static suite => string.Equals(suite.Key, "cache", StringComparison.OrdinalIgnoreCase));
            if (includesCache && suites.Count == 1)
                return "feature/fallback behavior";

            return includesCache
                ? "mixed feature/fallback + internal feature behavior"
                : "feature behavior";
        }

        var includesHotPath = suites.Any(suite => HotPathComparisonSuites.Contains(suite.Key));
        var includesNonHotPath = suites.Any(suite => !HotPathComparisonSuites.Contains(suite.Key));

        if (includesHotPath && !includesNonHotPath)
            return "hot-path comparison";
        if (includesHotPath)
            return "mixed comparison coverage";

        return "extended parity comparison";
    }

    private static bool TryParseAudience(string value, out BenchmarkSuiteAudience audience)
    {
        if (string.Equals(value, "featuresets", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "features", StringComparison.OrdinalIgnoreCase))
        {
            audience = BenchmarkSuiteAudience.FeatureSet;
            return true;
        }

        if (string.Equals(value, "compare", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "comparisons", StringComparison.OrdinalIgnoreCase))
        {
            audience = BenchmarkSuiteAudience.Comparison;
            return true;
        }

        audience = default;
        return false;
    }

    private static bool IsOption(string value)
        => value.StartsWith("-", StringComparison.Ordinal);
}

public enum BenchmarkSuiteAudience
{
    FeatureSet = 1,
    Comparison = 2
}

public sealed record BenchmarkSuiteDefinition(
    string Key,
    BenchmarkSuiteAudience Audience,
    string Description,
    string[] Filters,
    KeyValuePair<string, string>[] EnvironmentDefaults)
{
    public string DisplayName => $"{Audience}: {Key}";
}

public sealed record BenchmarkInvocationPlan(
    string DisplayName,
    string ReportAudience,
    string[] Arguments,
    KeyValuePair<string, string>[] EnvironmentDefaults);
