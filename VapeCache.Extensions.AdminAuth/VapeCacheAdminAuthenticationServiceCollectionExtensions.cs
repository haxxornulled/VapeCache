using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace VapeCache.Extensions.AdminAuth;

public static class VapeCacheAdminAuthenticationServiceCollectionExtensions
{
    private const string JwtSectionPath = "Authentication:JwtBearer";

    public static IServiceCollection AddVapeCacheAdminAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        bool requireAdminAuthorization,
        string authorizationPolicy = "VapeCacheAdmin",
        bool allowAnonymousAdminPolicy = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(authorizationPolicy))
            throw new ArgumentException("Authorization policy name is required.", nameof(authorizationPolicy));

        var jwtSection = configuration.GetSection(JwtSectionPath);
        var jwtEnabled = jwtSection.GetValue<bool>("Enabled");
        var authenticationBuilder = jwtEnabled
            ? services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            : services.AddAuthentication();

        if (jwtEnabled)
            ConfigureJwtBearer(authenticationBuilder, jwtSection);

        services
            .AddOptions<AdminAuthenticationValidationOptions>()
            .Configure(options =>
            {
                options.RequireAdminAuthorization = requireAdminAuthorization;
            });
        services.AddSingleton<IAuthenticationSchemeReadinessProbe, AuthenticationSchemeReadinessProbe>();
        services.AddHostedService<AdminAuthenticationStartupValidator>();

        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                authorizationPolicy,
                policy =>
                {
                    if (allowAnonymousAdminPolicy)
                    {
                        policy.RequireAssertion(static _ => true);
                        return;
                    }

                    policy.RequireAuthenticatedUser();
                });
        });

        return services;
    }

    private static void ConfigureJwtBearer(
        AuthenticationBuilder authenticationBuilder,
        IConfigurationSection jwtSection)
    {
        var authority = jwtSection["Authority"]?.Trim();
        var audience = jwtSection["Audience"]?.Trim();
        var signingKey = jwtSection["SigningKey"]?.Trim();
        var validIssuer = jwtSection["ValidIssuer"]?.Trim();
        var validAudience = jwtSection["ValidAudience"]?.Trim();
        var requireHttpsMetadata = jwtSection.GetValue("RequireHttpsMetadata", true);

        if (!string.IsNullOrWhiteSpace(authority) && !string.IsNullOrWhiteSpace(signingKey))
        {
            throw new InvalidOperationException(
                "Configure either Authentication:JwtBearer:Authority or Authentication:JwtBearer:SigningKey, not both.");
        }

        if (string.IsNullOrWhiteSpace(authority) && string.IsNullOrWhiteSpace(signingKey))
        {
            throw new InvalidOperationException(
                "Authentication:JwtBearer:Enabled=true requires either Authentication:JwtBearer:Authority " +
                "or Authentication:JwtBearer:SigningKey.");
        }

        if (!string.IsNullOrWhiteSpace(authority))
        {
            authenticationBuilder.AddJwtBearer(options =>
            {
                options.RequireHttpsMetadata = requireHttpsMetadata;
                options.MapInboundClaims = false;
                options.Authority = authority;
                if (!string.IsNullOrWhiteSpace(audience))
                    options.Audience = audience;
            });
            return;
        }

        if (string.IsNullOrWhiteSpace(validAudience) && string.IsNullOrWhiteSpace(audience))
        {
            throw new InvalidOperationException(
                "Authentication:JwtBearer symmetric-key mode requires Authentication:JwtBearer:Audience " +
                "or Authentication:JwtBearer:ValidAudience.");
        }

        var keyBytes = Encoding.UTF8.GetBytes(signingKey!);
        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                "Authentication:JwtBearer:SigningKey must be at least 32 bytes (256 bits).");
        }

        var resolvedAudience = string.IsNullOrWhiteSpace(validAudience) ? audience : validAudience;
        authenticationBuilder.AddJwtBearer(options =>
        {
            options.RequireHttpsMetadata = requireHttpsMetadata;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
                ValidateIssuer = !string.IsNullOrWhiteSpace(validIssuer),
                ValidIssuer = string.IsNullOrWhiteSpace(validIssuer) ? null : validIssuer,
                ValidateAudience = true,
                ValidAudience = resolvedAudience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(2)
            };
        });
    }
}
