namespace VapeCache.Core.Policies;

/// <summary>
/// Canonical policy for cache tag normalization and reserved zone tags.
/// </summary>
public static class CacheTagPolicy
{
    public const string ZonePrefix = "zone:";
    private const int LinearDedupThreshold = 8;

    public static string ToZoneTag(string zone)
    {
        if (!TryGetTrimmedRange(zone, out var start, out var length))
            throw new ArgumentException("Zone must not be null or whitespace.", nameof(zone));

        return string.Create(
            ZonePrefix.Length + length,
            (zone, start, length),
            static (destination, state) =>
            {
                ZonePrefix.AsSpan().CopyTo(destination);
                state.zone.AsSpan(state.start, state.length).CopyTo(destination[ZonePrefix.Length..]);
            });
    }

    public static string NormalizeTag(string tag)
    {
        if (!TryGetTrimmedRange(tag, out var start, out var length))
            throw new ArgumentException("Tag must not be null or whitespace.", nameof(tag));

        return NormalizeFromRange(tag, start, length);
    }

    public static string[] NormalizeTags(string[]? existingTags, string[]? additionalTags)
    {
        var maxCandidateCount = (existingTags?.Length ?? 0) + (additionalTags?.Length ?? 0);
        if (maxCandidateCount == 0)
            return Array.Empty<string>();

        if (maxCandidateCount <= LinearDedupThreshold)
            return NormalizeTagsLinear(existingTags, additionalTags, maxCandidateCount);

        return NormalizeTagsHash(existingTags, additionalTags, maxCandidateCount);
    }

    private static string[] NormalizeTagsLinear(string[]? existingTags, string[]? additionalTags, int maxCandidateCount)
    {
        var result = GC.AllocateUninitializedArray<string>(maxCandidateCount);
        var count = 0;

        AddNormalizedLinear(existingTags, result, ref count);
        AddNormalizedLinear(additionalTags, result, ref count);
        return FinalizeNormalizedResult(result, count);
    }

    private static string[] NormalizeTagsHash(string[]? existingTags, string[]? additionalTags, int maxCandidateCount)
    {
        var seen = new HashSet<string>(maxCandidateCount, StringComparer.Ordinal);
        var result = GC.AllocateUninitializedArray<string>(maxCandidateCount);
        var count = 0;

        AddNormalizedHash(existingTags, seen, result, ref count);
        AddNormalizedHash(additionalTags, seen, result, ref count);
        return FinalizeNormalizedResult(result, count);
    }

    private static void AddNormalizedLinear(string[]? tags, string[] result, ref int count)
    {
        if (tags is null)
            return;

        foreach (var tag in tags)
        {
            if (tag is null || !TryGetTrimmedRange(tag, out var start, out var length))
                continue;

            var normalizedSpan = tag.AsSpan(start, length);
            if (ContainsNormalized(result, count, normalizedSpan))
                continue;

            result[count++] = NormalizeFromRange(tag, start, length);
        }
    }

    private static void AddNormalizedHash(string[]? tags, HashSet<string> seen, string[] result, ref int count)
    {
        if (tags is null)
            return;

        foreach (var tag in tags)
        {
            if (tag is null || !TryGetTrimmedRange(tag, out var start, out var length))
                continue;

            var normalized = NormalizeFromRange(tag, start, length);
            if (seen.Add(normalized))
                result[count++] = normalized;
        }
    }

    private static string[] FinalizeNormalizedResult(string[] result, int count)
    {
        if (count == 0)
            return Array.Empty<string>();

        if (count == result.Length)
            return result;

        var normalized = GC.AllocateUninitializedArray<string>(count);
        Array.Copy(result, normalized, count);
        return normalized;
    }

    private static string NormalizeFromRange(string value, int start, int length)
    {
        if (start == 0 && length == value.Length)
            return value;

        return value.Substring(start, length);
    }

    private static bool ContainsNormalized(string[] values, int count, ReadOnlySpan<char> candidate)
    {
        for (var i = 0; i < count; i++)
        {
            if (candidate.Equals(values[i].AsSpan(), StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool TryGetTrimmedRange(string value, out int start, out int length)
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
}
