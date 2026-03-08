using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace VapeCache.Extensions.Aspire.Autofac;

internal sealed class ConfigurationReloadingOptionsMonitor<T> : IOptions<T>, IOptionsMonitor<T>, IDisposable
    where T : class
{
    private readonly Func<T> _valueFactory;
    private readonly IDisposable _changeRegistration;
    private readonly System.Threading.Lock _gate = new();
    private Action<T, string?>? _listeners;
    private T _currentValue;
    private bool _disposed;

    public ConfigurationReloadingOptionsMonitor(IConfiguration configuration, Func<T> valueFactory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _valueFactory = valueFactory ?? throw new ArgumentNullException(nameof(valueFactory));
        _currentValue = _valueFactory();
        _changeRegistration = ChangeToken.OnChange(configuration.GetReloadToken, ReloadFromConfiguration);
    }

    public T Value => CurrentValue;

    public T CurrentValue => Volatile.Read(ref _currentValue);

    public T Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<T, string?> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_gate)
        {
            ThrowIfDisposed();
            _listeners += listener;
        }

        return new Subscription(this, listener);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _listeners = null;
        }

        _changeRegistration.Dispose();
    }

    private void ReloadFromConfiguration()
    {
        T nextValue;
        try
        {
            nextValue = _valueFactory();
        }
        catch
        {
            // Preserve last-known-good value when a hot reload is temporarily invalid.
            return;
        }

        Volatile.Write(ref _currentValue, nextValue);

        Action<T, string?>? callbacks;
        lock (_gate)
        {
            if (_disposed)
                return;

            callbacks = _listeners;
        }

        if (callbacks is null)
            return;

        var delegates = callbacks.GetInvocationList();
        for (var i = 0; i < delegates.Length; i++)
        {
            if (delegates[i] is not Action<T, string?> callback)
                continue;

            try
            {
                callback(nextValue, Options.DefaultName);
            }
            catch
            {
                // OnChange listeners should not destabilize the host.
            }
        }
    }

    private void Unsubscribe(Action<T, string?> listener)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _listeners -= listener;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class Subscription(
        ConfigurationReloadingOptionsMonitor<T> owner,
        Action<T, string?> listener) : IDisposable
    {
        private ConfigurationReloadingOptionsMonitor<T>? _owner = owner;
        private Action<T, string?>? _listener = listener;

        public void Dispose()
        {
            var localOwner = Interlocked.Exchange(ref _owner, null);
            var localListener = Interlocked.Exchange(ref _listener, null);
            if (localOwner is null || localListener is null)
                return;

            localOwner.Unsubscribe(localListener);
        }
    }
}
