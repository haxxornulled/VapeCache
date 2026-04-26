using System.Threading;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching;

internal sealed class CacheOperationOriginAccessor : ICacheOperationOriginAccessor
{
    private static readonly AsyncLocal<string?> Current = new();

    public string CurrentOrigin => string.IsNullOrWhiteSpace(Current.Value)
        ? CacheOperationOrigin.Native
        : Current.Value!;

    public IDisposable BeginScope(string origin)
    {
        var previous = Current.Value;
        Current.Value = string.IsNullOrWhiteSpace(origin) ? CacheOperationOrigin.Native : origin;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;
        private int _disposed;

        public Scope(string? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            Current.Value = _previous;
        }
    }
}
