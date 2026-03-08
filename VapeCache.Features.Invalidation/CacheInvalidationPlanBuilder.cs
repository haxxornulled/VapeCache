namespace VapeCache.Features.Invalidation;

/// <summary>
/// Accumulates invalidation targets and builds a normalized plan.
/// </summary>
public sealed class CacheInvalidationPlanBuilder
{
    private readonly HashSet<string> _tags = new(StringComparer.Ordinal);
    private readonly HashSet<string> _zones = new(StringComparer.Ordinal);
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    /// <summary>
    /// Executes add plan.
    /// </summary>
    public CacheInvalidationPlanBuilder AddPlan(CacheInvalidationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        AddTags(plan.Tags);
        AddZones(plan.Zones);
        AddKeys(plan.Keys);
        return this;
    }

    /// <summary>
    /// Executes add tags.
    /// </summary>
    public CacheInvalidationPlanBuilder AddTags(IEnumerable<string>? tags)
    {
        AddTargets(_tags, tags);
        return this;
    }

    /// <summary>
    /// Executes add zones.
    /// </summary>
    public CacheInvalidationPlanBuilder AddZones(IEnumerable<string>? zones)
    {
        AddTargets(_zones, zones);
        return this;
    }

    /// <summary>
    /// Executes add keys.
    /// </summary>
    public CacheInvalidationPlanBuilder AddKeys(IEnumerable<string>? keys)
    {
        AddTargets(_keys, keys);
        return this;
    }

    /// <summary>
    /// Executes build.
    /// </summary>
    public CacheInvalidationPlan Build()
    {
        if (_tags.Count == 0 && _zones.Count == 0 && _keys.Count == 0)
            return CacheInvalidationPlan.Empty;

        return new CacheInvalidationPlan(_tags, _zones, _keys);
    }

    private static void AddTargets(HashSet<string> destination, IEnumerable<string>? values)
    {
        if (values is null)
            return;

        foreach (var value in values)
        {
            if (!TryGetTrimmedRange(value, out var start, out var length))
                continue;

            destination.Add(NormalizeFromRange(value, start, length));
        }
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
