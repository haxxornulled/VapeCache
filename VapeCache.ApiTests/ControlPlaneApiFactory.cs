using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace VapeCache.ApiTests;

public sealed class ControlPlaneApiFactory(bool requireApiKey) : WebApplicationFactory<Program>
{
    public const string ApiKeyHeaderName = "X-VapeCache-ApiKey";
    public const string ApiKey = "integration-test-key";

    private readonly string _stateDirectory = Path.Combine(
        Path.GetTempPath(),
        "vapecache-api-tests",
        Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            Directory.CreateDirectory(_stateDirectory);

            config.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false);
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RevocationControlPlane:RequireApiKey"] = requireApiKey ? "true" : "false",
                ["RevocationControlPlane:ApiKeyHeaderName"] = ApiKeyHeaderName,
                ["RevocationControlPlane:ApiKey"] = ApiKey,
                ["RevocationControlPlane:PersistencePath"] = Path.Combine(_stateDirectory, "revocations-state.json")
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing || !Directory.Exists(_stateDirectory))
            return;

        try
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
        catch
        {
        }
    }
}
