namespace VapeCache.Features.Invalidation;

/// <summary>
/// Entity-centric invalidation policy for common CRUD/event-driven scenarios.
/// Produces entity tags and optional key removals from entity identifiers.
/// </summary>
public sealed class EntityInvalidationPolicy<TEvent> : ICacheInvalidationPolicy<TEvent>
{
    private static readonly string[] NoPrefixes = [];

    private readonly string _entityName;
    private readonly Func<TEvent, IEnumerable<string>?> _idsSelector;
    private readonly Func<TEvent, IEnumerable<string>?>? _zonesSelector;
    private readonly string[] _keyPrefixes;
    private readonly Func<TEvent, bool>? _predicate;

    public EntityInvalidationPolicy(
        string entityName,
        Func<TEvent, IEnumerable<string>?> idsSelector,
        Func<TEvent, IEnumerable<string>?>? zonesSelector = null,
        IEnumerable<string>? keyPrefixes = null,
        Func<TEvent, bool>? predicate = null)
    {
        if (string.IsNullOrWhiteSpace(entityName))
            throw new ArgumentException("Entity name is required.", nameof(entityName));

        _entityName = entityName.Trim();
        _idsSelector = idsSelector ?? throw new ArgumentNullException(nameof(idsSelector));
        _zonesSelector = zonesSelector;
        _predicate = predicate;
        _keyPrefixes = NormalizePrefixes(keyPrefixes);
    }

    public ValueTask<CacheInvalidationPlan> BuildPlanAsync(TEvent eventData, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_predicate is not null && !_predicate(eventData))
            return ValueTask.FromResult(CacheInvalidationPlan.Empty);

        var ids = _idsSelector(eventData);
        if (ids is null)
            return ValueTask.FromResult(CacheInvalidationPlan.Empty);

        var estimatedCount = ids is IReadOnlyCollection<string> sizedIds ? sizedIds.Count : 0;
        var tags = estimatedCount > 0 ? new List<string>(estimatedCount) : new List<string>();
        var keyCapacity = 0;
        if (estimatedCount > 0 && _keyPrefixes.Length > 0)
        {
            var candidateCapacity = (long)estimatedCount * _keyPrefixes.Length;
            keyCapacity = candidateCapacity < int.MaxValue ? (int)candidateCapacity : int.MaxValue;
        }

        var keys = keyCapacity > 0 ? new List<string>(keyCapacity) : new List<string>();
        foreach (var id in ids)
        {
            if (!TryGetTrimmedRange(id, out var start, out var length))
                continue;

            tags.Add(CreatePrefixedTarget(_entityName, id, start, length));
            for (var i = 0; i < _keyPrefixes.Length; i++)
            {
                keys.Add(CreatePrefixedTarget(_keyPrefixes[i], id, start, length));
            }
        }

        if (tags.Count == 0 && keys.Count == 0)
            return ValueTask.FromResult(CacheInvalidationPlan.Empty);

        var zones = _zonesSelector?.Invoke(eventData);
        return ValueTask.FromResult(new CacheInvalidationPlan(tags, zones, keys));
    }

    private string[] NormalizePrefixes(IEnumerable<string>? prefixes)
    {
        if (prefixes is null)
            return [_entityName];

        HashSet<string>? normalized = null;
        foreach (var prefix in prefixes)
        {
            if (!TryGetTrimmedRange(prefix, out var start, out var length))
                continue;

            normalized ??= new HashSet<string>(StringComparer.Ordinal);
            normalized.Add(NormalizeFromRange(prefix, start, length));
        }

        if (normalized is null || normalized.Count == 0)
            return NoPrefixes;

        var result = new string[normalized.Count];
        normalized.CopyTo(result);
        return result;
    }

    private static string CreatePrefixedTarget(string prefix, string source, int start, int length)
    {
        return string.Create(
            prefix.Length + 1 + length,
            (prefix, source, start, length),
            static (destination, state) =>
            {
                state.prefix.AsSpan().CopyTo(destination);
                destination[state.prefix.Length] = ':';
                state.source.AsSpan(state.start, state.length).CopyTo(destination[(state.prefix.Length + 1)..]);
            });
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
