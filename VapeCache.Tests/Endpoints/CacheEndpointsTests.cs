using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VapeCache.Abstractions.Caching;
using VapeCache.Console.Hosting;
using VapeCache.Infrastructure.Caching;
using Xunit;

namespace VapeCache.Tests.Endpoints;

public sealed class CacheEndpointsTests
{
    [Fact]
    public async Task Cache_endpoints_roundtrip_and_sets_backend_header()
    {
        using var host = await CreateTestHostAsync();
        var client = host.GetTestClient();

        var key = "k1";
        var payload = "hello";

        var put = new HttpRequestMessage(HttpMethod.Put, $"/cache/{key}?ttlSeconds=60")
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/plain")
        };
        var putResp = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        Assert.True(putResp.Headers.Contains("X-Cache-Backend"));

        var getResp = await client.GetAsync($"/cache/{key}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        Assert.True(getResp.Headers.Contains("X-Cache-Backend"));
        Assert.Equal("application/octet-stream", getResp.Content.Headers.ContentType?.MediaType);
        var bytes = await getResp.Content.ReadAsByteArrayAsync();
        Assert.Equal(payload, Encoding.UTF8.GetString(bytes));

        var delResp = await client.DeleteAsync($"/cache/{key}");
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);
        Assert.True(delResp.Headers.Contains("X-Cache-Backend"));

        var missing = await client.GetAsync($"/cache/{key}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var stats = await client.GetAsync("/cache/stats");
        Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
    }

    private static async Task<IHost> CreateTestHostAsync()
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddMemoryCache();
                    services.AddSingleton<ICurrentCacheService, CurrentCacheService>();
                    services.AddSingleton<CacheStatsRegistry>();
                    services.AddSingleton<ICacheStats, CurrentCacheStats>();
                    services.AddSingleton<ICacheService, InMemoryCacheService>();
                });
                web.Configure(app =>
                {
                    CacheEndpoints.Configure(app);
                });
            })
            .StartAsync();

        return host;
    }
}
