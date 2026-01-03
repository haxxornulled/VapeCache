using System.Text;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace VapeCache.Benchmarks;

/// <summary>
/// Exports a compact comparison table (SER vs VapeCache) into comparison.md for the same run.
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
        var sb = new StringBuilder();
        sb.AppendLine("# Redis Client Comparison (SER vs VapeCache)");
        sb.AppendLine();
        sb.AppendLine("| Category | Payload | Client | Mean (µs) | Alloc B/op | Gen0/op | Ratio vs Baseline |");
        sb.AppendLine("|----------|---------|--------|-----------|------------|---------|-------------------|");

        foreach (var benchmark in summary.BenchmarksCases)
        {
            var report = summary[benchmark];
            if (report is null)
                continue;

            var stats = report.ResultStatistics;
            var meanUs = stats is null ? double.NaN : stats.Mean / 1_000.0;
            var alloc = report.GcStats.GetBytesAllocatedPerOperation(benchmark);
            var gen0 = report.GcStats.Gen0Collections;
            var allocText = alloc.ToString();

            var payload = benchmark.Parameters["PayloadBytes"]?.ToString() ?? "";
            var category = string.Join(",", benchmark.Descriptor.Categories.OrderBy(c => c));
            var client = benchmark.Descriptor.WorkloadMethodDisplayInfo;

            // Find baseline within same category/payload.
            var baselineCandidate = summary.BenchmarksCases.FirstOrDefault(b =>
                string.Join(",", b.Descriptor.Categories.OrderBy(c => c)) == category &&
                Equals(b.Parameters["PayloadBytes"], benchmark.Parameters["PayloadBytes"]) &&
                (b.Descriptor.WorkloadMethodDisplayInfo.Contains("SER_", StringComparison.OrdinalIgnoreCase) || b.Descriptor.Baseline));

            double? ratio = null;
            if (baselineCandidate is not null && !ReferenceEquals(baselineCandidate, benchmark))
            {
                var baselineReport = summary[baselineCandidate];
                var baselineMean = baselineReport?.ResultStatistics?.Mean;
                if (baselineMean is not null && baselineMean > 0)
                    ratio = (stats?.Mean ?? 0) / baselineMean.Value;
            }

            sb.Append('|')
              .Append(category).Append('|')
              .Append(payload).Append('|')
              .Append(client).Append('|')
              .Append(meanUs.ToString("0.00")).Append('|')
              .Append(allocText).Append('|')
              .Append(gen0.ToString("0.###")).Append('|')
              .Append(ratio is null ? "-" : ratio.Value.ToString("0.###")).Append('|')
              .AppendLine();
        }

        return sb.ToString();
    }
}
