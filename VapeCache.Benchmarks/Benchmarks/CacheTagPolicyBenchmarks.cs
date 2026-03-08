using BenchmarkDotNet.Attributes;
using VapeCache.Core.Policies;

namespace VapeCache.Benchmarks.Benchmarks;

[MemoryDiagnoser]
public class CacheTagPolicyBenchmarks
{
    private string[] _existing = Array.Empty<string>();
    private string[] _additional = Array.Empty<string>();
    private string _trimmedZone = string.Empty;
    private string _cleanTag = string.Empty;

    [Params(4, 12, 32)]
    public int TagCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _existing = new string[TagCount];
        _additional = new string[TagCount];
        var duplicateModulo = Math.Max(1, TagCount / 3);

        for (var i = 0; i < TagCount; i++)
        {
            var existingTag = $"tenant:{i % duplicateModulo}";
            _existing[i] = (i & 1) == 0 ? existingTag : $" {existingTag} ";

            var incomingTag = $"entity:{i % duplicateModulo}";
            _additional[i] = i % 5 == 0
                ? " "
                : (i & 1) == 0 ? incomingTag : $" {incomingTag} ";
        }

        _trimmedZone = "  grocery:products  ";
        _cleanTag = "tenant:42";
    }

    [Benchmark(Baseline = true, Description = "Normalize tags (legacy)")]
    public string[] NormalizeTags_Legacy()
        => LegacyNormalizeTags(_existing, _additional);

    [Benchmark(Description = "Normalize tags (current)")]
    public string[] NormalizeTags_Current()
        => CacheTagPolicy.NormalizeTags(_existing, _additional);

    [Benchmark(Description = "Zone tag generation")]
    public string ToZoneTag()
        => CacheTagPolicy.ToZoneTag(_trimmedZone);

    [Benchmark(Description = "Single-tag normalization")]
    public string NormalizeTag()
        => CacheTagPolicy.NormalizeTag(_cleanTag);

    private static string[] LegacyNormalizeTags(string[]? existingTags, string[]? additionalTags)
    {
        var existingCount = existingTags?.Length ?? 0;
        var additionalCount = additionalTags?.Length ?? 0;
        if (existingCount == 0 && additionalCount == 0)
            return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(existingCount + additionalCount);

        AddNormalized(existingTags);
        AddNormalized(additionalTags);
        return result.ToArray();

        void AddNormalized(string[]? tags)
        {
            if (tags is null)
                return;

            foreach (var tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag))
                    continue;

                var normalized = tag.Trim();
                if (seen.Add(normalized))
                    result.Add(normalized);
            }
        }
    }
}
