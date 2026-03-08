namespace VapeCache.Features.Invalidation;

/// <summary>
/// Immutable invalidation targets resolved by one or more policies.
/// </summary>
public sealed class CacheInvalidationPlan
{
    private static readonly string[] EmptyTargets = [];

    public static CacheInvalidationPlan Empty { get; } = new();

    public IReadOnlyList<string> Tags { get; }

    public IReadOnlyList<string> Zones { get; }

    public IReadOnlyList<string> Keys { get; }

    public int TotalTargets => Tags.Count + Zones.Count + Keys.Count;

    public CacheInvalidationPlan(
        IEnumerable<string>? tags = null,
        IEnumerable<string>? zones = null,
        IEnumerable<string>? keys = null)
    {
        Tags = NormalizeTargets(tags);
        Zones = NormalizeTargets(zones);
        Keys = NormalizeTargets(keys);
    }

    private static string[] NormalizeTargets(IEnumerable<string>? values)
    {
        if (values is null)
            return EmptyTargets;

        HashSet<string>? normalized = null;
        foreach (var value in values)
        {
            if (!TryGetTrimmedRange(value, out var start, out var length))
                continue;

            var trimmed = NormalizeFromRange(value, start, length);
            normalized ??= new HashSet<string>(StringComparer.Ordinal);
            normalized.Add(trimmed);
        }

        if (normalized is null || normalized.Count == 0)
            return EmptyTargets;

        var result = new string[normalized.Count];
        normalized.CopyTo(result);
        return result;
    }

    private static bool TryGetTrimmedRange(string? value, out int start, out int length)
    {
        if (string.IsNullOrEmpty(value))
        {
            start = 0;
            length = 0;
            return false;
        }

        var span = value.AsSpan();
        var left = 0;
        var right = span.Length - 1;
        while (left <= right && char.IsWhiteSpace(span[left]))
            left++;

        while (right >= left && char.IsWhiteSpace(span[right]))
            right--;

        if (left > right)
        {
            start = 0;
            length = 0;
            return false;
        }

        start = left;
        length = right - left + 1;
        return true;
    }

    private static string NormalizeFromRange(string value, int start, int length)
    {
        if (start == 0 && length == value.Length)
            return value;

        return value.Substring(start, length);
    }
}
