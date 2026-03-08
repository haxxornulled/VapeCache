namespace VapeCache.Abstractions.Diagnostics;

/// <summary>
/// Represents the backend type.
/// </summary>
public enum BackendType
{
    /// <summary>
    /// Specifies redis.
    /// </summary>
    Redis = 0,
    /// <summary>
    /// Specifies n memory.
    /// </summary>
    InMemory = 1
}

/// <summary>
/// Represents the backend type resolver.
/// </summary>
public static class BackendTypeResolver
{
    /// <summary>
    /// Executes try parse name.
    /// </summary>
    public static bool TryParseName(string? value, out BackendType backend)
    {
        if (value is not null && TryParseName(value.AsSpan(), out backend))
        {
            return true;
        }

        backend = default;
        return false;
    }

    /// <summary>
    /// Executes try parse name.
    /// </summary>
    public static bool TryParseName(ReadOnlySpan<char> value, out BackendType backend)
    {
        var candidate = value.Trim();
        if (candidate.IsEmpty)
        {
            backend = default;
            return false;
        }

        if (candidate.Equals("redis", StringComparison.OrdinalIgnoreCase))
        {
            backend = BackendType.Redis;
            return true;
        }

        if (candidate.Equals("memory", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("in-memory", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("in_memory", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("inmemory", StringComparison.OrdinalIgnoreCase))
        {
            backend = BackendType.InMemory;
            return true;
        }

        backend = default;
        return false;
    }

    /// <summary>
    /// Executes to wire name.
    /// </summary>
    public static string ToWireName(this BackendType backend)
        => backend == BackendType.Redis ? "redis" : "in-memory";

    /// <summary>
    /// Executes to gauge value.
    /// </summary>
    public static int ToGaugeValue(this BackendType backend)
        => backend == BackendType.Redis ? 1 : 0;

    /// <summary>
    /// Executes resolve.
    /// </summary>
    public static BackendType Resolve(string? currentBackend, bool breakerOpen, bool forcedOpen)
    {
        if (forcedOpen || breakerOpen)
            return BackendType.InMemory;

        if (TryParseName(currentBackend, out var parsed))
            return parsed;

        return BackendType.Redis;
    }
}
