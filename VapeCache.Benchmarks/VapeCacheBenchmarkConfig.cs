using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Validators;

namespace VapeCache.Benchmarks;

public sealed class VapeCacheBenchmarkConfig : ManualConfig
{
    public static IConfig Instance { get; } = new VapeCacheBenchmarkConfig();

    private VapeCacheBenchmarkConfig()
    {
        AddJob(CreateJob()
            .WithPlatform(Platform.X64)
            .WithJit(Jit.RyuJit)
            .WithId(GetRunProfile()));

        AddLogger(ConsoleLogger.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddDiagnoser(MemoryDiagnoser.Default);
        AddExporter(MarkdownExporter.GitHub);
        AddExporter(HtmlExporter.Default);
        AddExporter(CsvExporter.Default);
        AddValidator(JitOptimizationsValidator.FailOnError);

        SummaryStyle = SummaryStyle.Default.WithRatioStyle(RatioStyle.Trend);
        Orderer = new DefaultOrderer(SummaryOrderPolicy.FastestToSlowest);

        WithOption(ConfigOptions.JoinSummary, true);
        WithOption(ConfigOptions.DisableLogFile, false);
    }

    private static Job CreateJob()
        => string.Equals(GetRunProfile(), "smoke", StringComparison.OrdinalIgnoreCase)
            ? Job.ShortRun
            : Job.Default;

    private static string GetRunProfile()
        => Environment.GetEnvironmentVariable("VAPECACHE_BENCH_RUN_PROFILE")?.Trim().ToLowerInvariant() switch
        {
            "full" => "full",
            _ => "smoke"
        };
}
