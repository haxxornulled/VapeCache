using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace VapeCache.ApiTests;

public sealed class HealthEndpointsApiTests
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task Health_endpoints_return_ok(string path)
    {
        using var factory = new ControlPlaneApiFactory(requireApiKey: true);
        using var client = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync(path);
        Assert.True(response.IsSuccessStatusCode, $"{path} failed with {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task Root_endpoint_returns_service_status()
    {
        using var factory = new ControlPlaneApiFactory(requireApiKey: true);
        using var client = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        var payload = await client.GetFromJsonAsync<RootStatusResponse>("/");

        Assert.NotNull(payload);
        Assert.Equal("VapeCache.Licensing.ControlPlane", payload.Service);
        Assert.Equal("ok", payload.Status);
    }

    [Fact]
    public async Task Health_service_includes_revocation_registry_readiness_check()
    {
        using var factory = new ControlPlaneApiFactory(requireApiKey: true);
        using var client = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        var healthService = factory.Services.GetRequiredService<HealthCheckService>();
        var report = await healthService.CheckHealthAsync();

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Contains("revocation-registry", report.Entries.Keys);
    }

    private sealed record RootStatusResponse(string? Service, string? Status);
}
