using Microsoft.Extensions.Options;

namespace VapeCache.Infrastructure.DependencyInjection;

internal sealed class StaticOptionsMonitor<T> : IOptions<T>, IOptionsMonitor<T>
    where T : class
{
    private readonly T _value;

    public StaticOptionsMonitor(T value)
    {
        _value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public T Value => _value;

    public T CurrentValue => _value;

    /// <summary>
    /// Gets value.
    /// </summary>
    public T Get(string? name) => _value;

    /// <summary>
    /// Executes value.
    /// </summary>
    public IDisposable OnChange(Action<T, string?> listener) => NoopDisposable.Instance;

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        /// <summary>
        /// Releases resources used by the current instance.
        /// </summary>
        public void Dispose() { }
    }
}
