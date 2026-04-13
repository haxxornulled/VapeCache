namespace VapeCache.Benchmarks;

public static class BenchmarkCommandLine
{
    private const string IncludeLiveFlag = "--include-live";
    private const string LiveBenchmarksEnvVar = "VAPECACHE_BENCH_INCLUDE_LIVE";
    private const string MicroCategory = "Micro";

    public static string[] BuildEffectiveArgs(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var includeLive = ShouldIncludeLiveBenchmarks(args);
        var sanitized = args
            .Where(static arg => !string.Equals(arg, IncludeLiveFlag, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!includeLive && !HasExplicitSelection(sanitized))
        {
            sanitized.Add("--anyCategories");
            sanitized.Add(MicroCategory);
        }

        return sanitized.ToArray();
    }

    private static bool ShouldIncludeLiveBenchmarks(IReadOnlyCollection<string> args)
        => args.Any(static arg => string.Equals(arg, IncludeLiveFlag, StringComparison.OrdinalIgnoreCase)) ||
           bool.TryParse(Environment.GetEnvironmentVariable(LiveBenchmarksEnvVar), out var includeLive) && includeLive;

    private static bool HasExplicitSelection(IReadOnlyCollection<string> args)
        => args.Any(static arg =>
            string.Equals(arg, "--filter", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--filters", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--anyCategories", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--allCategories", StringComparison.OrdinalIgnoreCase));
}
