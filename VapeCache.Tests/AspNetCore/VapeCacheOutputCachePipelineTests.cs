using System.Buffers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using VapeCache.Abstractions.Caching;
using VapeCache.Extensions.AspNetCore;

namespace VapeCache.Tests.AspNetCore;

public sealed class VapeCacheOutputCachePipelineTests
{
    [Fact]
    public async Task UseVapeCacheOutputCaching_CachesEndpointResponses()
    {
        var counter = 0;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ICacheService, InMemoryRawCacheService>();
        builder.Services.AddVapeCacheOutputCaching(options =>
        {
            options.AddBasePolicy(policy => policy.Expire(TimeSpan.FromSeconds(20)));
        });

        var app = builder.Build();
        app.UseVapeCacheOutputCaching();
        app.MapGet("/api/counter", () => Interlocked.Increment(ref counter).ToString())
            .CacheWithVapeCache();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var first = await client.GetStringAsync("/api/counter");
        var second = await client.GetStringAsync("/api/counter");

        Assert.Equal("1", first);
        Assert.Equal("1", second);
        Assert.Equal(1, counter);

        await app.StopAsync();
    }

    private sealed class InMemoryRawCacheService : ICacheService
    {
        private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();

        public string Name => "in-memory-raw";

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
        {
            lock (_gate)
            {
                return ValueTask.FromResult(_store.TryGetValue(key, out var payload) ? payload : null);
            }
        }

        public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct)
        {
            lock (_gate)
            {
                _store[key] = value.ToArray();
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> RemoveAsync(string key, CancellationToken ct)
        {
            lock (_gate)
            {
                return ValueTask.FromResult(_store.Remove(key));
            }
        }

        public ValueTask<T?> GetAsync<T>(string key, SpanDeserializer<T> deserialize, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask SetAsync<T>(string key, T value, Action<IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<T> GetOrSetAsync<T>(string key, Func<CancellationToken, ValueTask<T>> factory, Action<IBufferWriter<byte>, T> serialize, SpanDeserializer<T> deserialize, CacheEntryOptions options, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
