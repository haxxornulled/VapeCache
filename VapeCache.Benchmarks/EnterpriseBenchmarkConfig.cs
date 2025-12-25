using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;

namespace VapeCache.Benchmarks;

public sealed class EnterpriseBenchmarkConfig : ManualConfig
{
    public EnterpriseBenchmarkConfig()
    {
        WithUnionRule(ConfigUnionRule.AlwaysUseLocal);
        WithOptions(ConfigOptions.DisableLogFile | ConfigOptions.JoinSummary);
        WithSummaryStyle(
            SummaryStyle.Default
                .WithZeroMetricValuesInContent()
                .WithMaxParameterColumnWidth(48));

        AddLogger(new ResultsOnlyLogger(ConsoleLogger.Default));

        // Do not hard-code a runtime version here.
        // The benchmarks are compiled for the current TargetFramework (net10.0),
        // and forcing an older runtime would break execution.
        AddJob(
            Job.Default
                .WithId("net10-wks")
                .WithToolchain(InProcessNoEmitToolchain.Instance));

        AddJob(
            Job.Default
                .WithId("net10-svr")
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .WithGcServer(true));

        AddDiagnoser(MemoryDiagnoser.Default);

        AddExporter(CsvExporter.Default, HtmlExporter.Default, MarkdownExporter.GitHub, JsonExporter.FullCompressed);

        AddColumnProvider(DefaultColumnProviders.Instance);
        AddLogicalGroupRules(BenchmarkLogicalGroupRule.ByCategory);
    }

    private sealed class ResultsOnlyLogger : ILogger
    {
        private readonly ILogger _inner;
        private bool _pendingNewLine;
        private bool _inTableLine;

        public ResultsOnlyLogger(ILogger inner) => _inner = inner;

        public string Id => _inner.Id;
        public int Priority => _inner.Priority;

        public void Write(LogKind logKind, string text)
        {
            if (ShouldPrint(logKind, text) || _inTableLine)
            {
                _inner.Write(logKind, text);
                _pendingNewLine = true;

                if (StartsTableLine(text))
                    _inTableLine = true;
            }
        }

        public void WriteLine()
        {
            if (_pendingNewLine)
            {
                _inner.WriteLine();
                _pendingNewLine = false;
            }

            _inTableLine = false;
        }

        public void WriteLine(LogKind logKind, string text)
        {
            if (ShouldPrint(logKind, text) || _inTableLine)
            {
                _inner.WriteLine(logKind, text);
                _pendingNewLine = false;
                _inTableLine = false;
            }
        }

        public void Flush() => _inner.Flush();

        private static bool ShouldPrint(LogKind logKind, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            // Always show errors/warnings (fixes "silent fail").
            if (logKind is LogKind.Error or LogKind.Warning)
                return true;

            // Summary tables + exporter output are what we want on console.
            if (StartsTableLine(text))
                return true;

            if (text.Contains("BenchmarkDotNet.Artifacts", StringComparison.OrdinalIgnoreCase))
                return true;

            // Common "actionable failure" text can be written with non-error kinds; still show it.
            if (text.Contains("Set VAPECACHE_REDIS_HOST", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("ERROR(S):", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static bool StartsTableLine(string text)
        {
            var trimmed = text.TrimStart();
            return trimmed.StartsWith("|", StringComparison.Ordinal);
        }
    }
}
