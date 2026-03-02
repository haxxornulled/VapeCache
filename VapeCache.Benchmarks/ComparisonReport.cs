using System.Text;

namespace VapeCache.Benchmarks;

internal enum ComparisonClient
{
    Unknown = 0,
    StackExchangeRedis = 1,
    VapeCache = 2
}

internal sealed record ComparisonSample(
    string Suite,
    string Scenario,
    string Parameters,
    ComparisonClient Client,
    double MeanMicroseconds,
    long AllocatedBytesPerOperation);

internal sealed record ComparisonRow(
    string Suite,
    string Scenario,
    string Parameters,
    double StackExchangeMeanMicroseconds,
    double VapeCacheMeanMicroseconds,
    long StackExchangeAllocatedBytesPerOperation,
    long VapeCacheAllocatedBytesPerOperation,
    double RatioVapeToStackExchange,
    double DeltaPercent,
    string Winner);

internal static class ComparisonReport
{
    private const string ReportAudienceEnvironmentVariable = "VAPECACHE_BENCH_REPORT_AUDIENCE";

    public static ComparisonClient DetectClient(string? methodName)
    {
        if (string.IsNullOrWhiteSpace(methodName))
            return ComparisonClient.Unknown;

        if (methodName.StartsWith("SER_", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("StackExchange", StringComparison.OrdinalIgnoreCase) ||
            methodName.Contains("StackExchange", StringComparison.OrdinalIgnoreCase))
        {
            return ComparisonClient.StackExchangeRedis;
        }

        if (methodName.StartsWith("Ours_", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("VapeCache", StringComparison.OrdinalIgnoreCase) ||
            methodName.Contains("VapeCache", StringComparison.OrdinalIgnoreCase))
        {
            return ComparisonClient.VapeCache;
        }

        return ComparisonClient.Unknown;
    }

    public static IReadOnlyList<ComparisonRow> BuildRows(IEnumerable<ComparisonSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        return samples
            .Where(sample => sample.Client is ComparisonClient.StackExchangeRedis or ComparisonClient.VapeCache)
            .GroupBy(sample => (sample.Suite, sample.Scenario, sample.Parameters), StringTupleComparer.Instance)
            .Select(BuildRow)
            .Where(row => row is not null)
            .Select(row => row!)
            .OrderBy(row => row.Suite, StringComparer.Ordinal)
            .ThenBy(row => row.Scenario, StringComparer.Ordinal)
            .ThenBy(row => row.Parameters, StringComparer.Ordinal)
            .ToArray();
    }

    public static string BuildMarkdown(IEnumerable<ComparisonSample> samples)
    {
        var rows = BuildRows(samples);
        var sb = new StringBuilder();
        var reportAudience = ResolveReportAudience();

        sb.AppendLine("# Redis Head-to-Head Comparison");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(reportAudience))
        {
            sb.AppendLine($"Reporting audience: {reportAudience}");
            sb.AppendLine();
        }

        if (rows.Count == 0)
        {
            sb.AppendLine("No comparable StackExchange.Redis and VapeCache benchmark pairs were found.");
            return sb.ToString();
        }

        var vapeWins = rows.Count(row => string.Equals(row.Winner, "VapeCache", StringComparison.Ordinal));
        var stackWins = rows.Count(row => string.Equals(row.Winner, "StackExchange.Redis", StringComparison.Ordinal));
        var ties = rows.Count(row => string.Equals(row.Winner, "Tie", StringComparison.Ordinal));

        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"- Comparisons: {rows.Count}");
        sb.AppendLine($"- VapeCache faster: {vapeWins}");
        sb.AppendLine($"- StackExchange.Redis faster: {stackWins}");
        sb.AppendLine($"- Ties: {ties}");
        sb.AppendLine();

        sb.AppendLine("## Results");
        sb.AppendLine();
        sb.AppendLine("| Suite | Scenario | Params | SER Mean (us) | VapeCache Mean (us) | Ratio (Vape/SER) | Delta % | Winner | SER Alloc B/op | VapeCache Alloc B/op |");
        sb.AppendLine("|-------|----------|--------|---------------|---------------------|------------------|---------|--------|----------------|----------------------|");

        foreach (var row in rows)
        {
            sb.Append('|')
              .Append(EscapeCell(row.Suite)).Append('|')
              .Append(EscapeCell(row.Scenario)).Append('|')
              .Append(EscapeCell(row.Parameters)).Append('|')
              .Append(row.StackExchangeMeanMicroseconds.ToString("0.00")).Append('|')
              .Append(row.VapeCacheMeanMicroseconds.ToString("0.00")).Append('|')
              .Append(row.RatioVapeToStackExchange.ToString("0.000")).Append('|')
              .Append(row.DeltaPercent.ToString("0.0")).Append("%|")
              .Append(row.Winner).Append('|')
              .Append(row.StackExchangeAllocatedBytesPerOperation).Append('|')
              .Append(row.VapeCacheAllocatedBytesPerOperation).Append('|')
              .AppendLine();
        }

        return sb.ToString();
    }

    private static ComparisonRow? BuildRow(IGrouping<(string Suite, string Scenario, string Parameters), ComparisonSample> group)
    {
        var stack = group.FirstOrDefault(sample => sample.Client == ComparisonClient.StackExchangeRedis);
        var vape = group.FirstOrDefault(sample => sample.Client == ComparisonClient.VapeCache);
        if (stack is null || vape is null)
            return null;

        if (stack.MeanMicroseconds <= 0 || double.IsNaN(stack.MeanMicroseconds) ||
            vape.MeanMicroseconds <= 0 || double.IsNaN(vape.MeanMicroseconds))
        {
            return null;
        }

        var ratio = vape.MeanMicroseconds / stack.MeanMicroseconds;
        var deltaPercent = ((vape.MeanMicroseconds - stack.MeanMicroseconds) / stack.MeanMicroseconds) * 100.0;
        var winner = ResolveWinner(deltaPercent);

        return new ComparisonRow(
            group.Key.Suite,
            group.Key.Scenario,
            group.Key.Parameters,
            stack.MeanMicroseconds,
            vape.MeanMicroseconds,
            stack.AllocatedBytesPerOperation,
            vape.AllocatedBytesPerOperation,
            ratio,
            deltaPercent,
            winner);
    }

    private static string ResolveWinner(double deltaPercent)
    {
        if (Math.Abs(deltaPercent) < 0.5)
            return "Tie";

        return deltaPercent < 0
            ? "VapeCache"
            : "StackExchange.Redis";
    }

    private static string EscapeCell(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal);

    private static string? ResolveReportAudience()
    {
        var raw = Environment.GetEnvironmentVariable(ReportAudienceEnvironmentVariable);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Suite, string Scenario, string Parameters)>
    {
        public static readonly StringTupleComparer Instance = new();

        public bool Equals((string Suite, string Scenario, string Parameters) x, (string Suite, string Scenario, string Parameters) y)
            => string.Equals(x.Suite, y.Suite, StringComparison.Ordinal) &&
               string.Equals(x.Scenario, y.Scenario, StringComparison.Ordinal) &&
               string.Equals(x.Parameters, y.Parameters, StringComparison.Ordinal);

        public int GetHashCode((string Suite, string Scenario, string Parameters) obj)
            => HashCode.Combine(obj.Suite, obj.Scenario, obj.Parameters);
    }
}
