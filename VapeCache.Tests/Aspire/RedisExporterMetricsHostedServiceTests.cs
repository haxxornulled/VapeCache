using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VapeCache.Extensions.Aspire;
using VapeCache.Extensions.Aspire.Hosting;

namespace VapeCache.Tests.Aspire;

public sealed class RedisExporterMetricsHostedServiceTests
{
    [Fact]
    public async Task ExecuteAsync_RespectsRuntimeEnableDisableToggles()
    {
        var optionsMonitor = new MutableOptionsMonitor(new RedisExporterMetricsOptions
        {
            Enabled = false,
            Endpoint = "http://localhost:9121/metrics",
            PollInterval = TimeSpan.FromMilliseconds(30),
            RequestTimeout = TimeSpan.FromMilliseconds(100)
        });

        var handler = new CountingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("redis_up 1\nredis_connected_clients 7\nredis_commands_processed_total 99\n")
        });
        using var client = new HttpClient(handler);
        var factory = new StaticHttpClientFactory(client);
        var state = new RedisExporterMetricsState();
        var sut = new RedisExporterMetricsHostedService(
            factory,
            optionsMonitor,
            state,
            NullLogger<RedisExporterMetricsHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(120);

            Assert.False(state.Current.Enabled);
            Assert.Equal(0, handler.RequestCount);

            optionsMonitor.Set(new RedisExporterMetricsOptions
            {
                Enabled = true,
                Endpoint = "http://localhost:9121/metrics",
                PollInterval = TimeSpan.FromMilliseconds(30),
                RequestTimeout = TimeSpan.FromMilliseconds(100)
            });

            await Task.Delay(180);

            Assert.True(state.Current.Enabled);
            Assert.Equal(1, state.Current.Up);
            Assert.True(handler.RequestCount > 0);
            Assert.Equal(7d, state.Current.ConnectedClients);
            Assert.Equal(99L, state.Current.CommandsProcessedTotal);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_SetsFailure_WhenEndpointIsInvalid()
    {
        var optionsMonitor = new MutableOptionsMonitor(new RedisExporterMetricsOptions
        {
            Enabled = true,
            Endpoint = "not-a-uri",
            PollInterval = TimeSpan.FromMilliseconds(30),
            RequestTimeout = TimeSpan.FromMilliseconds(100)
        });

        var handler = new CountingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("redis_up 1")
        });
        using var client = new HttpClient(handler);
        var factory = new StaticHttpClientFactory(client);
        var state = new RedisExporterMetricsState();
        var sut = new RedisExporterMetricsHostedService(
            factory,
            optionsMonitor,
            state,
            NullLogger<RedisExporterMetricsHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(120);
            Assert.True(state.Current.Enabled);
            Assert.Equal(0, state.Current.Up);
            Assert.Equal(0, handler.RequestCount);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CountingHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class MutableOptionsMonitor : IOptionsMonitor<RedisExporterMetricsOptions>
    {
        private readonly System.Threading.Lock _gate = new();
        private event Action<RedisExporterMetricsOptions, string?>? Changed;
        private RedisExporterMetricsOptions _current;

        public MutableOptionsMonitor(RedisExporterMetricsOptions current)
        {
            _current = current;
        }

        public RedisExporterMetricsOptions CurrentValue
        {
            get
            {
                lock (_gate)
                    return _current;
            }
        }

        public RedisExporterMetricsOptions Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<RedisExporterMetricsOptions, string?> listener)
        {
            lock (_gate)
            {
                Changed += listener;
            }

            return new ChangeSubscription(this, listener);
        }

        public void Set(RedisExporterMetricsOptions next)
        {
            Action<RedisExporterMetricsOptions, string?>? listeners;
            lock (_gate)
            {
                _current = next;
                listeners = Changed;
            }

            listeners?.Invoke(next, Options.DefaultName);
        }

        private void Unsubscribe(Action<RedisExporterMetricsOptions, string?> listener)
        {
            lock (_gate)
            {
                Changed -= listener;
            }
        }

        private sealed class ChangeSubscription(
            MutableOptionsMonitor owner,
            Action<RedisExporterMetricsOptions, string?> listener) : IDisposable
        {
            private MutableOptionsMonitor? _owner = owner;
            private Action<RedisExporterMetricsOptions, string?>? _listener = listener;

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
}
