using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;

namespace VapeCache.Benchmarks;

/// <summary>
/// Exports a compact comparison table (StackExchange.Redis vs VapeCache) into comparison.md for the same run.
/// </summary>
internal sealed class ComparisonMarkdownExporter : IExporter
{
    public static readonly IExporter Default = new ComparisonMarkdownExporter();

    public string Name => "comparison-markdown";

    public void ExportToLog(Summary summary, ILogger logger)
    {
        var content = Build(summary);
        logger.WriteLine(content);
    }

    public IEnumerable<string> ExportToFiles(Summary summary, ILogger logger)
    {
        var file = Path.Combine(summary.ResultsDirectoryPath, "comparison.md");
        var content = Build(summary);
        File.WriteAllText(file, content);
        logger.WriteLineInfo($"Comparison markdown exported: {file}");
        return new[] { file };
    }

    private static string Build(Summary summary)
    {
        var samples = ExtractSamples(summary);
        return ComparisonReport.BuildMarkdown(samples);
    }

    private static IReadOnlyList<ComparisonSample> ExtractSamples(Summary summary)
    {
        var samples = new List<ComparisonSample>(summary.BenchmarksCases.Length);
        foreach (var benchmark in summary.BenchmarksCases)
        {
            var report = summary[benchmark];
            var stats = report?.ResultStatistics;
            if (stats is null)
                continue;

            var client = ComparisonReport.DetectClient(benchmark.Descriptor.WorkloadMethod.Name);
            if (client == ComparisonClient.Unknown)
                continue;

            var meanUs = stats.Mean / 1_000.0;
            var alloc = report!.GcStats.GetBytesAllocatedPerOperation(benchmark) ?? 0;
            var scenario = ResolveScenario(benchmark);
            var parameters = BuildParameters(benchmark);

            samples.Add(
                new ComparisonSample(
                    Suite: benchmark.Descriptor.Type.Name,
                    Scenario: scenario,
                    Parameters: parameters,
                    Client: client,
                    MeanMicroseconds: meanUs,
                    AllocatedBytesPerOperation: alloc));
        }

        return samples;
    }

    private static string ResolveScenario(BenchmarkDotNet.Running.BenchmarkCase benchmark)
    {
        var operation = benchmark.Parameters["Operation"]?.ToString();
        if (!string.IsNullOrWhiteSpace(operation))
            return operation;

        var firstCategory = benchmark.Descriptor.Categories.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(firstCategory))
            return firstCategory;

        var methodName = benchmark.Descriptor.WorkloadMethod.Name;
        if (methodName.StartsWith("SER_", StringComparison.OrdinalIgnoreCase))
            return methodName["SER_".Length..];
        if (methodName.StartsWith("Ours_", StringComparison.OrdinalIgnoreCase))
            return methodName["Ours_".Length..];
        if (methodName.StartsWith("StackExchange_", StringComparison.OrdinalIgnoreCase))
            return methodName["StackExchange_".Length..];
        if (methodName.StartsWith("VapeCache_", StringComparison.OrdinalIgnoreCase))
            return methodName["VapeCache_".Length..];

        return methodName;
    }

    private static string BuildParameters(BenchmarkDotNet.Running.BenchmarkCase benchmark)
    {
        if (benchmark.Parameters.Items.Count == 0)
            return "-";

        var parts = new List<string>(benchmark.Parameters.Items.Count);
        foreach (var item in benchmark.Parameters.Items)
        {
            if (string.Equals(item.Name, "Operation", StringComparison.Ordinal))
                continue;

            var valueText = item.Value?.ToString() ?? "null";
            parts.Add($"{item.Name}={valueText}");
        }

        return parts.Count == 0 ? "-" : string.Join(", ", parts);
    }
}
