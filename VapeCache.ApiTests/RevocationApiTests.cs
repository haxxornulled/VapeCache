using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using VapeCache.Licensing.ControlPlane.Revocation;

namespace VapeCache.ApiTests;

public sealed class RevocationApiTests
{
    [Fact]
    public async Task Snapshot_requires_api_key_when_enabled()
    {
        using var factory = new ControlPlaneApiFactory(requireApiKey: true);
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/api/v1/revocations/snapshot");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Revocation_endpoints_round_trip_with_valid_api_key()
    {
        using var factory = new ControlPlaneApiFactory(requireApiKey: true);
        using var client = CreateClient(factory);
        client.DefaultRequestHeaders.Add(ControlPlaneApiFactory.ApiKeyHeaderName, ControlPlaneApiFactory.ApiKey);

        var licenseId = $"license-{Guid.NewGuid():N}";

        var revokeResponse = await client.PostAsJsonAsync(
            $"/api/v1/revocations/licenses/{licenseId}/revoke",
            new RevocationMutationRequest("integration-revoke", "api-tests"));
        revokeResponse.EnsureSuccessStatusCode();

        var revokePayload = await revokeResponse.Content.ReadFromJsonAsync<RevocationMutationResponse>();
        Assert.NotNull(revokePayload);
        Assert.True(revokePayload.Revoked);
        Assert.Equal(licenseId, revokePayload.Identity);

        var revokedStatus = await client.GetFromJsonAsync<RevocationStatusResponse>($"/api/v1/revocations/status/{licenseId}");
        Assert.NotNull(revokedStatus);
        Assert.True(revokedStatus.Revoked);
        Assert.Equal("license", revokedStatus.Source);

        var activateResponse = await client.PostAsJsonAsync(
            $"/api/v1/revocations/licenses/{licenseId}/activate",
            new RevocationMutationRequest("integration-activate", "api-tests"));
        activateResponse.EnsureSuccessStatusCode();

        var activeStatus = await client.GetFromJsonAsync<RevocationStatusResponse>($"/api/v1/revocations/status/{licenseId}");
        Assert.NotNull(activeStatus);
        Assert.False(activeStatus.Revoked);
        Assert.Equal("none", activeStatus.Source);

        var snapshot = await client.GetFromJsonAsync<RevocationSnapshotResponse>("/api/v1/revocations/snapshot");
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.RevokedLicenses);
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
}
