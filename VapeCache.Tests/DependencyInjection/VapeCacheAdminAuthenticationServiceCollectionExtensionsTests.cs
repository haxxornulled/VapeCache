using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VapeCache.Extensions.AdminAuth;

namespace VapeCache.Tests.DependencyInjection;

public sealed class VapeCacheAdminAuthenticationServiceCollectionExtensionsTests
{
    [Fact]
    public void AddVapeCacheAdminAuthentication_Throws_WhenAuthorityAndSigningKeyAreBothSet()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Authentication:JwtBearer:Enabled"] = "true",
            ["Authentication:JwtBearer:Authority"] = "https://issuer.example.com/",
            ["Authentication:JwtBearer:SigningKey"] = "0123456789abcdef0123456789abcdef",
            ["Authentication:JwtBearer:Audience"] = "api://vapecache-admin"
        });
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddVapeCacheAdminAuthentication(configuration, requireAdminAuthorization: true));

        Assert.Contains("Authority", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SigningKey", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddVapeCacheAdminAuthentication_Throws_WhenSigningKeyIsTooShort()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Authentication:JwtBearer:Enabled"] = "true",
            ["Authentication:JwtBearer:SigningKey"] = "short-key",
            ["Authentication:JwtBearer:ValidAudience"] = "vapecache-admin"
        });
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddVapeCacheAdminAuthentication(configuration, requireAdminAuthorization: true));

        Assert.Contains("32 bytes", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddVapeCacheAdminAuthentication_FailsStartup_WhenAdminAuthRequired_AndNoSchemesRegistered()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Authentication:JwtBearer:Enabled"] = "false"
        });
        var services = new ServiceCollection();
        services.AddVapeCacheAdminAuthentication(configuration, requireAdminAuthorization: true);

        using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        Assert.NotEmpty(hostedServices);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            foreach (var hosted in hostedServices)
                await hosted.StartAsync(CancellationToken.None);
        });

        Assert.Contains("no authentication schemes", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddVapeCacheAdminAuthentication_AllowsStartup_WhenJwtAuthorityConfigured()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Authentication:JwtBearer:Enabled"] = "true",
            ["Authentication:JwtBearer:Authority"] = "https://issuer.example.com/",
            ["Authentication:JwtBearer:Audience"] = "api://vapecache-admin"
        });
        var services = new ServiceCollection();
        services.AddVapeCacheAdminAuthentication(configuration, requireAdminAuthorization: true);

        using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        foreach (var hosted in hostedServices)
            await hosted.StartAsync(CancellationToken.None);
    }

    [Fact]
    public void AddVapeCacheAdminAuthentication_RegistersRequireAuthenticatedUserPolicy()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Authentication:JwtBearer:Enabled"] = "false"
        });
        var services = new ServiceCollection();
        services.AddVapeCacheAdminAuthentication(
            configuration,
            requireAdminAuthorization: false,
            authorizationPolicy: "AdminPolicy",
            allowAnonymousAdminPolicy: false);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        var policy = options.GetPolicy("AdminPolicy");

        Assert.NotNull(policy);
        Assert.Contains(policy!.Requirements, static r => r is DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public void AddVapeCacheAdminAuthentication_RegistersAnonymousAssertionPolicy_WhenRequested()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Authentication:JwtBearer:Enabled"] = "false"
        });
        var services = new ServiceCollection();
        services.AddVapeCacheAdminAuthentication(
            configuration,
            requireAdminAuthorization: false,
            authorizationPolicy: "AdminPolicy",
            allowAnonymousAdminPolicy: true);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        var policy = options.GetPolicy("AdminPolicy");

        Assert.NotNull(policy);
        Assert.Contains(policy!.Requirements, static r => r is AssertionRequirement);
    }

    private static IConfiguration BuildConfiguration(IDictionary<string, string?> values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
