using Microsoft.Extensions.Options;

namespace VapeCache.Benchmarks;

internal sealed class BenchmarkOptionsMonitor<T> : IOptionsMonitor<T>
{
    private readonly T _value;

    public BenchmarkOptionsMonitor(T value)
    {
        _value = value;
    }

    public T CurrentValue => _value;
    public T Get(string? name) => _value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
