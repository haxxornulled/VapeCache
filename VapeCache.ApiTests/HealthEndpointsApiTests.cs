using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace VapeCache.ApiTests;

public sealed class HealthEndpointsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointsApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false);
            });
        });
    }

    [Fact]
    public async Task Health_endpoints_return_ok()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        var health = await client.GetAsync("/health");
        Assert.True(health.IsSuccessStatusCode, $"Health failed with {(int)health.StatusCode}.");

        var alive = await client.GetAsync("/alive");
        Assert.True(alive.IsSuccessStatusCode, $"Alive failed with {(int)alive.StatusCode}.");
    }
}
