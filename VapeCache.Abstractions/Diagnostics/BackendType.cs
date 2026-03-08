namespace VapeCache.Abstractions.Diagnostics;

public enum BackendType
{
    Redis = 0,
    InMemory = 1
}

public static class BackendTypeResolver
{
    public static bool TryParseName(string? value, out BackendType backend)
    {
        if (value is not null && TryParseName(value.AsSpan(), out backend))
        {
            return true;
        }

        backend = default;
        return false;
    }

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

    public static string ToWireName(this BackendType backend)
        => backend == BackendType.Redis ? "redis" : "in-memory";

    public static int ToGaugeValue(this BackendType backend)
        => backend == BackendType.Redis ? 1 : 0;

    public static BackendType Resolve(string? currentBackend, bool breakerOpen, bool forcedOpen)
    {
        if (forcedOpen || breakerOpen)
            return BackendType.InMemory;

        if (TryParseName(currentBackend, out var parsed))
            return parsed;

        return BackendType.Redis;
    }
}
